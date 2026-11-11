using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Framejet.Core.Video;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DMapFlags = SharpDX.Direct3D11.MapFlags;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// Converts a captured frame to RGB at source size, then scales it letterboxed into a render target:
    /// Catmull-Rom when upscaling, bilinear at 1:1 or when downscaling. Use it from one thread at a time.
    /// </summary>
    public sealed class FrameRenderer : IDisposable
    {
        const string ShaderResourceName = "Framejet.Platform.Video.Shaders.hlsl";
        static readonly Lazy<CompiledShaders> Shaders = new Lazy<CompiledShaders>(CompileShaders);
        static readonly RawColor4 Black = new RawColor4(0, 0, 0, 1);

        readonly D3DDevice _device;
        readonly DeviceContext _context;
        readonly VertexShader _vs;
        readonly PixelShader _nv12, _yuy2, _uyvy, _rgb32, _catmullRom, _bilinear;
        readonly SamplerState _linearClamp;

        CaptureFormat _format;
        PixelShader _convert;
        D3DBuffer _constants;
        Texture2D _plane0, _plane1;
        ShaderResourceView _plane0View, _plane1View;
        Texture2D _picture;
        RenderTargetView _pictureTarget;
        ShaderResourceView _pictureView;
        bool _disposed;

        /// <summary>How long this renderer's shaders took to get ready: the compile on the first one in the process, then only creating them. Diagnostic.</summary>
        public double ShaderMs { get; private set; }

        public FrameRenderer(D3DDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _context = device.ImmediateContext; // owned by the device, so not disposed here
            var shaderTimer = Stopwatch.StartNew();
            var code = Shaders.Value;
            try
            {
                _vs = new VertexShader(device, code.Vs);
                _nv12 = new PixelShader(device, code.Nv12);
                _yuy2 = new PixelShader(device, code.Yuy2);
                _uyvy = new PixelShader(device, code.Uyvy);
                _rgb32 = new PixelShader(device, code.Rgb32);
                _catmullRom = new PixelShader(device, code.CatmullRom);
                _bilinear = new PixelShader(device, code.Bilinear);
                ShaderMs = shaderTimer.Elapsed.TotalMilliseconds;
                _linearClamp = new SamplerState(device, new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue,
                });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Creates the source textures and the source-size RGB picture for this format.</summary>
        public void Configure(CaptureFormat format)
        {
            if (format == null) throw new ArgumentNullException(nameof(format));
            ThrowIfDisposed();
            ReleaseFormatResources();
            int w = format.Width, h = format.Height;
            switch (format.Format)
            {
                case GrabberFormat.Nv12:
                    _plane0 = CreateSourceTexture(w, h, Format.R8_UNorm);
                    _plane1 = CreateSourceTexture(w / 2, h / 2, Format.R8G8_UNorm);
                    _convert = _nv12;
                    break;
                case GrabberFormat.Yuy2:
                    _plane0 = CreateSourceTexture(w / 2, h, Format.R8G8B8A8_UNorm);
                    _convert = _yuy2;
                    break;
                case GrabberFormat.Uyvy:
                    _plane0 = CreateSourceTexture(w / 2, h, Format.R8G8B8A8_UNorm);
                    _convert = _uyvy;
                    break;
                case GrabberFormat.Rgb32:
                    _plane0 = CreateSourceTexture(w, h, Format.B8G8R8A8_UNorm);
                    _convert = _rgb32;
                    break;
                default:
                    throw new ArgumentException("Unknown format " + format.Format, nameof(format));
            }
            _plane0View = new ShaderResourceView(_device, _plane0);
            if (_plane1 != null) _plane1View = new ShaderResourceView(_device, _plane1);

            _picture = new Texture2D(_device, new Texture2DDescription
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None,
            });
            _pictureTarget = new RenderTargetView(_device, _picture);
            _pictureView = new ShaderResourceView(_device, _picture);

            // b0: rowR, rowG, rowB, then flipY and padding.
            var constants = new float[16];
            Array.Copy(ColorMatrix.YuvToRgb(h), constants, 12);
            constants[12] = format.Format == GrabberFormat.Rgb32 && format.BottomUp ? 1f : 0f;
            _constants = D3DBuffer.Create(_device, BindFlags.ConstantBuffer, constants);
            _format = format;
        }

        /// <summary>Copies a frame of <see cref="CaptureFormat.FrameBytes"/> bytes into the source textures.</summary>
        public void Upload(byte[] frame)
        {
            var format = ConfiguredFormat();
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Length < format.FrameBytes)
                throw new ArgumentException($"The frame has {frame.Length} bytes, not {format.FrameBytes}", nameof(frame));
            int w = format.Width, h = format.Height;
            switch (format.Format)
            {
                case GrabberFormat.Nv12:
                    CopyPlane(frame, 0, _plane0, w, h);
                    CopyPlane(frame, w * h, _plane1, w, h / 2);
                    break;
                case GrabberFormat.Yuy2:
                case GrabberFormat.Uyvy:
                    CopyPlane(frame, 0, _plane0, w * 2, h);
                    break;
                default:
                    CopyPlane(frame, 0, _plane0, w * 4, h);
                    break;
            }
        }

        /// <summary>Draws the uploaded frame letterboxed into a target, with black bars.</summary>
        public void Draw(RenderTargetView target, int targetWidth, int targetHeight)
        {
            var format = ConfiguredFormat();
            if (target == null) throw new ArgumentNullException(nameof(target));
            int w = format.Width, h = format.Height;

            _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            _context.InputAssembler.InputLayout = null;
            _context.VertexShader.Set(_vs);
            _context.PixelShader.SetSampler(0, _linearClamp);
            _context.PixelShader.SetConstantBuffer(0, _constants);

            // 1. Convert to RGB at source size.
            _context.PixelShader.SetShaderResource(0, _plane0View);
            _context.PixelShader.SetShaderResource(1, _plane1View);
            _context.OutputMerger.SetRenderTargets(_pictureTarget);
            _context.Rasterizer.SetViewport(0, 0, w, h, 0, 1);
            _context.PixelShader.Set(_convert);
            _context.Draw(3, 0);

            // 2. Clear the target black, 3. fit the picture's aspect ratio, 4. scale into that box.
            _context.OutputMerger.SetRenderTargets(target);
            _context.ClearRenderTargetView(target, Black);
            var box = Letterbox.Fit(w, h, targetWidth, targetHeight);
            _context.Rasterizer.SetViewport(box.X, box.Y, box.Width, box.Height, 0, 1);
            _context.PixelShader.Set(box.Width > w || box.Height > h ? _catmullRom : _bilinear);
            _context.PixelShader.SetShaderResource(0, _pictureView);
            _context.PixelShader.SetShaderResource(1, null);
            _context.Draw(3, 0);

            // Unbind the picture so the next conversion can render into it.
            _context.PixelShader.SetShaderResource(0, null);
        }

        /// <summary>Uploads and draws a frame into an offscreen target and returns its pixels as tightly packed BGRA.</summary>
        internal byte[] RenderToBgra(byte[] frame, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth), "The target needs a positive size");
            var description = new Texture2DDescription
            {
                Width = targetWidth, Height = targetHeight, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget, CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None,
            };
            using (var target = new Texture2D(_device, description))
            using (var targetView = new RenderTargetView(_device, target))
            {
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CpuAccessFlags = CpuAccessFlags.Read;
                using (var staging = new Texture2D(_device, description))
                {
                    Upload(frame);
                    Draw(targetView, targetWidth, targetHeight);
                    _context.OutputMerger.SetRenderTargets((RenderTargetView)null);
                    _context.CopyResource(target, staging);
                    var mapped = _context.MapSubresource(staging, 0, MapMode.Read, D3DMapFlags.None);
                    try
                    {
                        int rowBytes = targetWidth * 4;
                        var pixels = new byte[rowBytes * targetHeight];
                        for (int y = 0; y < targetHeight; y++)
                            Marshal.Copy(mapped.DataPointer + y * mapped.RowPitch, pixels, y * rowBytes, rowBytes);
                        return pixels;
                    }
                    finally
                    {
                        _context.UnmapSubresource(staging, 0);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseFormatResources();
            _linearClamp?.Dispose();
            _bilinear?.Dispose();
            _catmullRom?.Dispose();
            _rgb32?.Dispose();
            _uyvy?.Dispose();
            _yuy2?.Dispose();
            _nv12?.Dispose();
            _vs?.Dispose();
        }

        Texture2D CreateSourceTexture(int width, int height, Format format) => new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource, CpuAccessFlags = CpuAccessFlags.Write, OptionFlags = ResourceOptionFlags.None,
        });

        /// <summary>Writes rows into a dynamic texture, honouring its row pitch.</summary>
        void CopyPlane(byte[] source, int offset, Texture2D texture, int rowBytes, int rows)
        {
            var mapped = _context.MapSubresource(texture, 0, MapMode.WriteDiscard, D3DMapFlags.None);
            try
            {
                if (mapped.RowPitch == rowBytes) Marshal.Copy(source, offset, mapped.DataPointer, rowBytes * rows);
                else
                    for (int y = 0; y < rows; y++)
                        Marshal.Copy(source, offset + y * rowBytes, mapped.DataPointer + y * mapped.RowPitch, rowBytes);
            }
            finally
            {
                _context.UnmapSubresource(texture, 0);
            }
        }

        CaptureFormat ConfiguredFormat()
        {
            ThrowIfDisposed();
            return _format ?? throw new InvalidOperationException("Configure the renderer first");
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FrameRenderer));
        }

        void ReleaseFormatResources()
        {
            _format = null;
            _convert = null;
            Release(ref _constants);
            Release(ref _pictureView);
            Release(ref _pictureTarget);
            Release(ref _picture);
            Release(ref _plane1View);
            Release(ref _plane0View);
            Release(ref _plane1);
            Release(ref _plane0);
        }

        static void Release<T>(ref T item) where T : class, IDisposable
        {
            item?.Dispose();
            item = null;
        }

        sealed class CompiledShaders
        {
            public byte[] Vs, Nv12, Yuy2, Uyvy, Rgb32, CatmullRom, Bilinear;
        }

        /// <summary>Runs once per process; every renderer, including ones recreated after device loss, reuses the bytecode.</summary>
        static CompiledShaders CompileShaders()
        {
            string source;
            using (var stream = typeof(FrameRenderer).Assembly.GetManifestResourceStream(ShaderResourceName))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded resource " + ShaderResourceName);
                using (var reader = new StreamReader(stream)) source = reader.ReadToEnd();
            }
            return new CompiledShaders
            {
                Vs = Compile(source, "VS", "vs_5_0"),
                Nv12 = Compile(source, "PS_Nv12", "ps_5_0"),
                Yuy2 = Compile(source, "PS_Yuy2", "ps_5_0"),
                Uyvy = Compile(source, "PS_Uyvy", "ps_5_0"),
                Rgb32 = Compile(source, "PS_Rgb32", "ps_5_0"),
                CatmullRom = Compile(source, "PS_ScaleCatmullRom", "ps_5_0"),
                Bilinear = Compile(source, "PS_ScaleBilinear", "ps_5_0"),
            };
        }

        static byte[] Compile(string source, string entryPoint, string profile)
        {
            using (var result = ShaderBytecode.Compile(source, entryPoint, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None, "Shaders.hlsl"))
            {
                if (result.HasErrors || result.Bytecode == null)
                    throw new InvalidOperationException($"Shader {entryPoint} did not compile: {result.Message}");
                return result.Bytecode.Data;
            }
        }
    }
}
