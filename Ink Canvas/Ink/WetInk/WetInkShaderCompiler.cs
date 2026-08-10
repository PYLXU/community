using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 运行时 HLSL 编译。用系统自带的 d3dcompiler_47.dll（Win10 起随系统分发），
    /// 与 Shaders/LiquidGlassEffect 的做法一致，不引入额外 NuGet 依赖。
    /// </summary>
    internal static class WetInkShaderCompiler
    {
        /// <summary>编译 HLSL 源码，失败返回 null（调用方应回退旧管线）。</summary>
        public static byte[] Compile(string source, string entryPoint, string profile, string sourceName)
        {
            if (string.IsNullOrEmpty(source))
                return null;

            var sourceBytes = Encoding.ASCII.GetBytes(source);
            ID3DBlob code = null;
            ID3DBlob errors = null;

            try
            {
                var hr = D3DCompile(
                    sourceBytes,
                    new IntPtr(sourceBytes.Length),
                    sourceName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    entryPoint,
                    profile,
                    D3DCompileOptimizationLevel3,
                    0,
                    out code,
                    out errors);

                if (hr < 0)
                {
                    Debug.WriteLine(
                        $"[WetInk] D3DCompile({profile}) failed 0x{hr:X8}: {ReadBlobText(errors)}");
                    return null;
                }

                if (code == null)
                    return null;

                var size = (int)code.GetBufferSize();
                if (size <= 0)
                    return null;

                var bytecode = new byte[size];
                Marshal.Copy(code.GetBufferPointer(), bytecode, 0, size);
                return bytecode;
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[WetInk] d3dcompiler_47.dll unavailable: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WetInk] shader compile error: {ex}");
                return null;
            }
            finally
            {
                Release(code);
                Release(errors);
            }
        }

        private static string ReadBlobText(ID3DBlob blob)
        {
            if (blob == null)
                return "(no diagnostics)";
            try
            {
                var size = (int)blob.GetBufferSize();
                return size <= 0
                    ? "(empty diagnostics)"
                    : Marshal.PtrToStringAnsi(blob.GetBufferPointer(), size);
            }
            catch
            {
                return "(unreadable diagnostics)";
            }
        }

        private static void Release(ID3DBlob blob)
        {
            if (blob == null)
                return;
            try { Marshal.ReleaseComObject(blob); }
            catch { /* best effort */ }
        }

        /// <summary>D3DCOMPILE_OPTIMIZATION_LEVEL3</summary>
        private const uint D3DCompileOptimizationLevel3 = 1 << 15;

        [ComImport]
        [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig]
            IntPtr GetBufferPointer();

            [PreserveSig]
            IntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(
            byte[] srcData,
            IntPtr srcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string sourceName,
            IntPtr defines,
            IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            uint flags1,
            uint flags2,
            [MarshalAs(UnmanagedType.Interface)] out ID3DBlob code,
            [MarshalAs(UnmanagedType.Interface)] out ID3DBlob errorMsgs);
    }
}