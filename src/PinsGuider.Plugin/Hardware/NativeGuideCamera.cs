// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using PinsGuider.Engine.Core;

namespace PinsGuider.Plugin.Hardware;

/// <summary>Independent guide-camera connection through an existing PINS native SDK adapter.</summary>
internal sealed class NativeGuideCamera : ICameraSource, IGainRange, IDisposable
{
    private readonly Func<IReadOnlyList<ICamera>> discover;
    private readonly string requestedDevice;
    private readonly Func<string?> mainCameraId;
    private readonly Func<string?> connectedMainCameraId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan timeoutMargin;
    private ICamera? camera;

    public NativeGuideCamera(Func<IReadOnlyList<ICamera>> discover, string requestedDevice,
        Func<string?> mainCameraId, Func<string?> connectedMainCameraId, TimeSpan? timeoutMargin = null)
    {
        this.discover = discover;
        this.requestedDevice = requestedDevice;
        this.mainCameraId = mainCameraId;
        this.connectedMainCameraId = connectedMainCameraId;
        this.timeoutMargin = timeoutMargin ?? TimeSpan.FromSeconds(15);
    }

    public string Name => camera is { } c ? NativeCameraCatalog.Selection(c) : requestedDevice;
    public bool IsConnected => camera?.Connected == true;
    public int SensorWidth => camera?.CameraXSize ?? 0;
    public int SensorHeight => camera?.CameraYSize ?? 0;
    public double PixelSizeUm => camera is { } c && double.IsFinite(c.PixelSizeX) ? c.PixelSizeX : 0;
    public int MaxBinning => camera is { } c ? Math.Max(1, (int)Math.Min(c.MaxBinX, c.MaxBinY)) : 1;
    public ushort MaxAdu => 0;
    public int BitsPerPixel => camera?.BitDepth ?? 16;
    public int? GainMin => camera is { CanSetGain: true } c ? c.GainMin : null;
    public int? GainMax => camera is { CanSetGain: true } c ? c.GainMax : null;
    public int? CurrentGain => camera is { CanGetGain: true } c ? c.Gain : null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ConnectCoreAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        ct.ThrowIfCancellationRequested();
        var candidates = discover();
        var matches = string.IsNullOrWhiteSpace(requestedDevice)
            ? candidates.Where(c => !string.Equals(c.Id, mainCameraId(), StringComparison.OrdinalIgnoreCase)).ToList()
            : candidates.Where(c => string.Equals(NativeCameraCatalog.Selection(c), requestedDevice, StringComparison.Ordinal)
                || string.Equals(c.Id, requestedDevice, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            throw new GuideCameraException(matches.Count == 0
                ? $"Native guide camera '{requestedDevice}' was not found. Available: {string.Join(", ", candidates.Select(NativeCameraCatalog.Selection))}"
                : "Several native cameras are available. Select a specific guide camera.");
        var chosen = matches[0];
        if (string.Equals(chosen.Id, connectedMainCameraId(), StringComparison.OrdinalIgnoreCase))
            throw new GuideCameraException($"'{chosen.Name}' is connected as the imaging camera; disconnect it or choose a different guide camera.");
        camera = chosen;
        try
        {
            if (!await chosen.Connect(ct).ConfigureAwait(false))
                throw new GuideCameraException($"Could not connect native guide camera '{chosen.Name}'. Close any INDI connection to this camera first.");
            ct.ThrowIfCancellationRequested();
            Logger.Info($"NativeGuider: SDK guide camera '{Name}' connected: {SensorWidth}x{SensorHeight}, {BitsPerPixel} bit");
        }
        catch
        {
            DisconnectCore();
            throw;
        }
    }

    public async Task<GuideFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cam = camera;
            if (cam?.Connected != true) throw new GuideCameraException("Native guide camera is not connected.");
            if (request.Binning < 1 || request.Binning > MaxBinning)
                throw new GuideCameraException($"Binning {request.Binning} is not supported by '{cam.Name}'.");
            var started = DateTimeOffset.UtcNow;
            double seconds = Math.Clamp(request.ExposureMs / 1000.0, Math.Max(cam.ExposureMin, 0.001), cam.ExposureMax);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds) + timeoutMargin);
            try
            {
                // Full frames avoid vendor-specific ROI alignment/orientation differences. The engine still tracks
                // in its requested search region; hardware subframes can be added after per-SDK validation.
                cam.EnableSubSample = false;
                cam.SetBinning((short)request.Binning, (short)request.Binning);
                if (request.Gain is >= 0 && cam.CanSetGain) cam.Gain = Math.Clamp(request.Gain.Value, cam.GainMin, cam.GainMax);
                if (request.Offset is >= 0 && cam.CanSetOffset) cam.Offset = Math.Clamp(request.Offset.Value, cam.OffsetMin, cam.OffsetMax);
                cam.StartExposure(new CaptureSequence(seconds, CaptureSequence.ImageTypes.LIGHT, null,
                    new BinningMode((short)request.Binning, (short)request.Binning), 1)
                {
                    Gain = request.Gain is >= 0 && cam.CanSetGain ? Math.Clamp(request.Gain.Value, cam.GainMin, cam.GainMax) : -1,
                    Offset = request.Offset is >= 0 && cam.CanSetOffset ? Math.Clamp(request.Offset.Value, cam.OffsetMin, cam.OffsetMax) : -1
                });
                await cam.WaitUntilExposureIsReady(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                var exposure = await cam.DownloadExposure(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (exposure is null) throw new GuideCameraException("Native guide exposure returned no data.");
                var image = await exposure.ToImageData(cancelToken: timeout.Token).ConfigureAwait(false);
                var pixels = image.Data.FlatArray;
                if (pixels is null) throw new GuideCameraException("Native guide camera did not return 16-bit pixel data.");
                return new GuideFrame(image.Properties.Width, image.Properties.Height, (ushort[])pixels.Clone())
                {
                    BitsPerPixel = image.Properties.BitDepth,
                    ExposureMs = seconds * 1000,
                    Binning = request.Binning,
                    StartTime = started
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await AbortAsync().ConfigureAwait(false);
                throw new GuideCameraException("Native guide exposure timed out.");
            }
            catch (OperationCanceledException)
            {
                await AbortAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not GuideCameraException)
            {
                await AbortAsync().ConfigureAwait(false);
                throw new GuideCameraException($"Native guide exposure failed: {ex.Message}", ex);
            }
        }
        finally { gate.Release(); }
    }

    public Task AbortAsync()
    {
        try { camera?.AbortExposure(); }
        catch (Exception ex) { Logger.Debug($"NativeGuider: SDK abort failed: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { DisconnectCore(); await ConnectCoreAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await AbortAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { DisconnectCore(); }
        finally { gate.Release(); }
    }

    private void DisconnectCore()
    {
        var old = camera;
        camera = null;
        try { old?.Disconnect(); }
        catch (Exception ex) { Logger.Debug($"NativeGuider: SDK disconnect failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        DisconnectCore();
        gate.Dispose();
    }
}
