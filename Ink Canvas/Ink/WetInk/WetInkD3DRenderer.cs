using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using DXGIAlphaMode = Vortice.DXGI.AlphaMode;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// Direct3D11 + DirectComposition 湿墨渲染器（不使用 Direct2D）。
    ///
    /// 每条湿墨带由 WetInkGeometryBuilder 三角化成顶点缓冲，
    /// 用最小 HLSL 顶点/像素着色器绘制：
    ///   - Standard：普通 alpha 混合
    ///   - Laser：加法混合（辉光）
    ///
    /// 两条铁律（旧系统实证）：
    /// 1) FlipDiscard 的回退缓冲在 Present 后内容未定义 → 每帧必须先 Clear，
    ///    否则出现「重复 + 抖动」。
    /// 2) Present 用 DoNotWait，遇 DXGI_ERROR_WAS_STILL_DRAWING 静默丢帧，
    ///    渲染线程绝不阻塞等 vsync。
    /// </summary>
    internal sealed class WetInkD3DRenderer : IWetInkBatchRenderer
    {
        private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
        private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
        private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
        private const uint PresentDoNotWait = 0x00000002;

        private static readonly FeatureLevel[] FeatureLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        /// <summary>顶点着色器：DIP → NDC，颜色直通。</summary>
        private const string VertexShaderSource = @"
cbuffer Transform : register(b0)
{
    float2 InvViewport;   // 2 / viewportSizeInDip
    float2 Padding;
};
struct VSInput  { float2 pos : POSITION; float4 color : COLOR; };
struct VSOutput { float4 pos : SV_POSITION; float4 color : COLOR; };
VSOutput main(VSInput input)
{
    VSOutput o;
    float x = input.pos.x * InvViewport.x - 1.0;
    float y = 1.0 - input.pos.y * InvViewport.y;
    o.pos = float4(x, y, 0.0, 1.0);
    o.color = input.color;
    return o;
}";

        /// <summary>像素着色器：预乘 alpha 输出。</summary>
        private const string PixelShaderSource = @"
struct PSInput { float4 pos : SV_POSITION; float4 color : COLOR; };
float4 main(PSInput input) : SV_TARGET
{
    return float4(input.color.rgb * input.color.a, input.color.a);
}";

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private IDXGIDevice _dxgiDevice;
        private IDXGIFactory2 _dxgiFactory;
        private IDXGISwapChain1 _swapChain;
        private ID3D11RenderTargetView _renderTargetView;

        private IDCompositionDevice _compositionDevice;
        private IDCompositionTarget _compositionTarget;
        private IDCompositionVisual _compositionVisual;

        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _pixelShader;
        private ID3D11InputLayout _inputLayout;
        private ID3D11Buffer _vertexBuffer;
        private ID3D11Buffer _constantBuffer;
        private ID3D11BlendState _alphaBlendState;
        private ID3D11BlendState _additiveBlendState;
        private ID3D11RasterizerState _rasterizerState;

        private int _vertexBufferCapacity;

        private IntPtr _hwnd;
        private WetInkTargetSnapshot _target;
        private bool _deviceReady;
        private bool _disposed;

        /// <summary>当前在途的湿墨带：sessionId → 几何 + 样式。</summary>
        private readonly Dictionary<long, StrokeEntry> _strokes = new Dictionary<long, StrokeEntry>();

        private readonly List<long> _retiredThisFrame = new List<long>();

        private struct StrokeEntry
        {
            public WetInkRibbonGeometry Geometry;
            public WetInkStyleSnapshot Style;
            public long SnapshotVersion;
        }

        public bool IsDeviceReady => _deviceReady;

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------

        public void BindTarget(IntPtr hwnd, in WetInkTargetSnapshot target)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WetInkD3DRenderer));
            if (hwnd == IntPtr.Zero)
                throw new ArgumentException("hwnd must be valid", nameof(hwnd));

            ReleaseAll();

            _hwnd = hwnd;
            _target = target;

            CreateDeviceResources();
            CreateCompositionTree();
            CreateSwapChain();
            CreatePipelineResources();

            _deviceReady = true;
            PresentFrame();
        }

        public void UpdateTarget(in WetInkTargetSnapshot target)
        {
            if (_disposed)
                return;

            var previous = _target;
            _target = target;

            if (!_deviceReady)
                return;

            if (previous.WidthPixels != target.WidthPixels ||
                previous.HeightPixels != target.HeightPixels)
            {
                ResizeSwapChain();
            }

            PresentFrame();
        }

        // ------------------------------------------------------------------
        // command application
        // ------------------------------------------------------------------

        public IReadOnlyList<long> Apply(List<WetInkCommand> commands)
        {
            _retiredThisFrame.Clear();

            if (_disposed || !_deviceReady || commands == null || commands.Count == 0)
                return _retiredThisFrame;

            var needsPresent = false;

            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                switch (command.Kind)
                {
                    case WetInkCommandKind.BeginStroke:
                    case WetInkCommandKind.UpdateStroke:
                        _strokes[command.SessionId] = new StrokeEntry
                        {
                            Geometry = command.Geometry,
                            Style = command.Style,
                            SnapshotVersion = command.SnapshotVersion
                        };
                        needsPresent = true;
                        break;

                    case WetInkCommandKind.EndStroke:
                        // 抬笔后湿墨仍需留在屏幕上，直到干墨合成完成（防烘干闪变），
                        // 真正移除由 RetireStroke 触发。
                        if (command.Geometry != null && !command.Geometry.IsEmpty)
                        {
                            _strokes[command.SessionId] = new StrokeEntry
                            {
                                Geometry = command.Geometry,
                                Style = command.Style,
                                SnapshotVersion = command.SnapshotVersion
                            };
                            needsPresent = true;
                        }
                        break;

                    case WetInkCommandKind.CancelStroke:
                    case WetInkCommandKind.RetireStroke:
                        if (_strokes.Remove(command.SessionId))
                        {
                            _retiredThisFrame.Add(command.SessionId);
                            needsPresent = true;
                        }
                        else if (command.Kind == WetInkCommandKind.RetireStroke)
                        {
                            // 已不在渲染集合里也要回执，避免上层永远等不到。
                            _retiredThisFrame.Add(command.SessionId);
                        }
                        break;

                    case WetInkCommandKind.UpdateTarget:
                        UpdateTarget(command.Target);
                        break;

                    case WetInkCommandKind.Reset:
                        _strokes.Clear();
                        needsPresent = true;
                        break;

                    case WetInkCommandKind.Shutdown:
                        _strokes.Clear();
                        PresentFrame();
                        return _retiredThisFrame;
                }
            }

            if (needsPresent)
                PresentFrame();

            return _retiredThisFrame;
        }

        // ------------------------------------------------------------------
        // rendering
        // ------------------------------------------------------------------

        private void PresentFrame()
        {
            if (!_deviceReady || _swapChain == null || _renderTargetView == null)
                return;

            try
            {
                _context.OMSetRenderTargets(_renderTargetView);
                _context.RSSetViewport(0, 0, _target.WidthPixels, _target.HeightPixels);

                // 铁律 1：FlipDiscard 回退缓冲内容未定义，每帧必须清屏。
                _context.ClearRenderTargetView(_renderTargetView, new Color4(0f, 0f, 0f, 0f));

                if (_strokes.Count > 0)
                    DrawStrokes();

                // 铁律 2：DoNotWait，绝不阻塞渲染线程。
                var result = _swapChain.Present(0, PresentFlags.DoNotWait);
                if (result.Failure)
                {
                    if (result.Code == DxgiErrorWasStillDrawing)
                    {
                        // GPU 还在忙，静默丢弃这一帧。
                        return;
                    }
                    if (result.Code == DxgiErrorDeviceRemoved || result.Code == DxgiErrorDeviceReset)
                    {
                        Debug.WriteLine("[WetInk] D3D device lost");
                        _deviceReady = false;
                        return;
                    }
                }

                _compositionDevice?.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WetInk] present failed: {ex}");
                _deviceReady = false;
            }
        }

        private void DrawStrokes()
        {
            // 视口常量：DIP → NDC。
            var invViewport = new float[]
            {
                (float)(2.0 / Math.Max(1.0, _target.WidthPixels / _target.DpiScaleX)),
                (float)(2.0 / Math.Max(1.0, _target.HeightPixels / _target.DpiScaleY)),
                0f, 0f
            };
            UpdateConstantBuffer(invViewport);

            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.VSSetConstantBuffer(0, _constantBuffer);
            _context.IASetInputLayout(_inputLayout);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetState(_rasterizerState);

            foreach (var kv in _strokes)
            {
                var entry = kv.Value;
                if (entry.Geometry == null || entry.Geometry.IsEmpty)
                    continue;

                _context.OMSetBlendState(
                    entry.Style.RenderMode == WetInkRenderMode.Laser
                        ? _additiveBlendState
                        : _alphaBlendState);

                var vertexCount = UploadVertices(entry.Geometry);
                if (vertexCount <= 0)
                    continue;

                _context.IASetVertexBuffer(
                    0, _vertexBuffer, Marshal.SizeOf<GpuVertex>(), 0);
                _context.Draw(vertexCount, 0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuVertex
        {
            public float X;
            public float Y;
            public float R;
            public float G;
            public float B;
            public float A;
        }

        private int UploadVertices(WetInkRibbonGeometry geometry)
        {
            var count = geometry.VertexCount;
            if (count <= 0)
                return 0;

            EnsureVertexBufferCapacity(count);

            var gpu = new GpuVertex[count];
            for (var i = 0; i < count; i++)
            {
                var v = geometry.Vertices[i];
                var argb = v.ColorArgb;
                gpu[i] = new GpuVertex
                {
                    X = v.X,
                    Y = v.Y,
                    R = ((argb >> 16) & 0xFF) / 255f,
                    G = ((argb >> 8) & 0xFF) / 255f,
                    B = (argb & 0xFF) / 255f,
                    A = ((argb >> 24) & 0xFF) / 255f
                };
            }

            var mapped = _context.Map(_vertexBuffer, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
            try
            {
                var handle = GCHandle.Alloc(gpu, GCHandleType.Pinned);
                try
                {
                    var bytes = count * Marshal.SizeOf<GpuVertex>();
                    unsafe
                    {
                        Buffer.MemoryCopy(
                            handle.AddrOfPinnedObject().ToPointer(),
                            mapped.DataPointer.ToPointer(),
                            bytes,
                            bytes);
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                _context.Unmap(_vertexBuffer, 0);
            }

            return count;
        }

        private void EnsureVertexBufferCapacity(int vertexCount)
        {
            if (_vertexBuffer != null && _vertexBufferCapacity >= vertexCount)
                return;

            _vertexBuffer?.Dispose();
            _vertexBufferCapacity = Math.Max(1024, vertexCount * 2);

            _vertexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = _vertexBufferCapacity * Marshal.SizeOf<GpuVertex>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        private void UpdateConstantBuffer(float[] values)
        {
            var mapped = _context.Map(_constantBuffer, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
            try
            {
                Marshal.Copy(values, 0, mapped.DataPointer, values.Length);
            }
            finally
            {
                _context.Unmap(_constantBuffer, 0);
            }
        }

        // ------------------------------------------------------------------
        // device / swapchain setup
        // ------------------------------------------------------------------

        private void CreateDeviceResources()
        {
            D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out _device,
                out _,
                out _context).CheckError();

            _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            try
            {
                using var dxgiDevice1 = _dxgiDevice.QueryInterface<IDXGIDevice1>();
                // 只留 1 帧预渲染队列，压低输入到光子延迟。
                dxgiDevice1.SetMaximumFrameLatency(1);
            }
            catch
            {
                // 老驱动不支持则用默认帧延迟。
            }

            using var adapter = _dxgiDevice.GetAdapter();
            _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        }

        private void CreateCompositionTree()
        {
            _compositionDevice.CreateTargetForHwnd(_hwnd, true, out _compositionTarget).CheckError();
            _compositionDevice.CreateVisual(out _compositionVisual).CheckError();
            _compositionTarget.SetRoot(_compositionVisual).CheckError();
            _compositionDevice.Commit().CheckError();
        }

        private void CreateSwapChain()
        {
            var description = new SwapChainDescription1
            {
                Width = Math.Max(1, _target.WidthPixels),
                Height = Math.Max(1, _target.HeightPixels),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = DXGIAlphaMode.Premultiplied,
                Flags = SwapChainFlags.None
            };

            _swapChain = _dxgiFactory.CreateSwapChainForComposition(_device, description, null);
            _compositionVisual.SetContent(_swapChain).CheckError();
            _compositionDevice.Commit().CheckError();

            CreateRenderTargetView();
        }

        private void CreateRenderTargetView()
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderTargetView = _device.CreateRenderTargetView(backBuffer);
        }

        private void ResizeSwapChain()
        {
            if (_swapChain == null)
                return;

            _renderTargetView?.Dispose();
            _renderTargetView = null;

            _swapChain.ResizeBuffers(
                2,
                Math.Max(1, _target.WidthPixels),
                Math.Max(1, _target.HeightPixels),
                Format.B8G8R8A8_UNorm,
                SwapChainFlags.None).CheckError();

            CreateRenderTargetView();
        }

        private void CreatePipelineResources()
        {
            var vsBytecode = WetInkShaderCompiler.Compile(
                VertexShaderSource, "main", "vs_4_0", "WetInkVS");
            var psBytecode = WetInkShaderCompiler.Compile(
                PixelShaderSource, "main", "ps_4_0", "WetInkPS");

            if (vsBytecode == null || psBytecode == null)
                throw new InvalidOperationException("WetInk shader compilation failed");

            _vertexShader = _device.CreateVertexShader(vsBytecode);
            _pixelShader = _device.CreatePixelShader(psBytecode);

            var elements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 8, 0)
            };
            _inputLayout = _device.CreateInputLayout(elements, vsBytecode);

            _constantBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None
            });

            EnsureVertexBufferCapacity(4096);

            _alphaBlendState = _device.CreateBlendState(CreateBlendDescription(additive: false));
            _additiveBlendState = _device.CreateBlendState(CreateBlendDescription(additive: true));

            _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = false,
                DepthClipEnable = false,
                ScissorEnable = false,
                MultisampleEnable = false,
                AntialiasedLineEnable = false
            });
        }

        private static BlendDescription CreateBlendDescription(bool additive)
        {
            var description = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };

            // 几何已按预乘 alpha 输出，故源系数用 One。
            description.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.One,
                DestinationBlend = additive ? Blend.One : Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = additive ? Blend.One : Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };

            return description;
        }

        // ------------------------------------------------------------------
        // teardown
        // ------------------------------------------------------------------

        private void ReleaseAll()
        {
            _deviceReady = false;
            _strokes.Clear();

            SafeDispose(ref _rasterizerState);
            SafeDispose(ref _additiveBlendState);
            SafeDispose(ref _alphaBlendState);
            SafeDispose(ref _constantBuffer);
            SafeDispose(ref _vertexBuffer);
            _vertexBufferCapacity = 0;
            SafeDispose(ref _inputLayout);
            SafeDispose(ref _pixelShader);
            SafeDispose(ref _vertexShader);
            SafeDispose(ref _renderTargetView);
            SafeDispose(ref _swapChain);

            if (_compositionVisual != null)
            {
                try { _compositionVisual.SetContent(null); } catch { /* teardown */ }
            }
            SafeDispose(ref _compositionVisual);
            SafeDispose(ref _compositionTarget);
            if (_compositionDevice != null)
            {
                try { _compositionDevice.Commit(); } catch { /* teardown */ }
            }
            SafeDispose(ref _compositionDevice);

            SafeDispose(ref _dxgiFactory);
            SafeDispose(ref _dxgiDevice);
            SafeDispose(ref _context);
            SafeDispose(ref _device);
        }

        private static void SafeDispose<T>(ref T resource) where T : class, IDisposable
        {
            if (resource == null)
                return;
            try { resource.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[WetInk] dispose {typeof(T).Name}: {ex.Message}"); }
            resource = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseAll();
        }
    }
}