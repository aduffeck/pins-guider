// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using Moq;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NUnit.Framework;
using PinsGuider.Engine.Core;
using PinsGuider.Plugin.Hardware;

namespace PinsGuider.Plugin.Tests;

[TestFixture]
public class NativeGuideCameraTests
{
    private static Mock<ICamera> Camera(string id = "SVBony_123")
    {
        var camera = new Mock<ICamera>();
        bool connected = false;
        camera.SetupGet(c => c.Id).Returns(id);
        camera.SetupGet(c => c.Name).Returns("SV905C");
        camera.SetupGet(c => c.Connected).Returns(() => connected);
        camera.Setup(c => c.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(() => connected = true);
        camera.Setup(c => c.Disconnect()).Callback(() => connected = false);
        camera.SetupGet(c => c.CameraXSize).Returns(4);
        camera.SetupGet(c => c.CameraYSize).Returns(2);
        camera.SetupGet(c => c.MaxBinX).Returns(2);
        camera.SetupGet(c => c.MaxBinY).Returns(2);
        camera.SetupGet(c => c.ExposureMin).Returns(0.001);
        camera.SetupGet(c => c.ExposureMax).Returns(30);
        camera.SetupGet(c => c.BitDepth).Returns(12);
        camera.SetupGet(c => c.PixelSizeX).Returns(2.9);
        camera.Setup(c => c.WaitUntilExposureIsReady(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return camera;
    }

    [Test]
    public async Task Captures_raw_full_frame_with_native_bit_depth_and_independent_settings()
    {
        var cam = Camera();
        cam.SetupGet(c => c.CanSetGain).Returns(true);
        cam.SetupGet(c => c.GainMin).Returns(0);
        cam.SetupGet(c => c.GainMax).Returns(100);
        var pixels = new ushort[] { 1, 2, 3, 4, 4000, 6, 7, 8 };
        var data = new Mock<IImageData>();
        data.SetupGet(i => i.Data).Returns(new ImageArray(pixels));
        data.SetupGet(i => i.Properties).Returns(new ImageProperties(4, 2, 12, true, 20, 0));
        var exposure = new Mock<IExposureData>();
        exposure.Setup(e => e.ToImageData(It.IsAny<IProgress<NINA.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(data.Object);
        cam.Setup(c => c.DownloadExposure(It.IsAny<CancellationToken>())).ReturnsAsync(exposure.Object);
        using var guide = new NativeGuideCamera(() => [cam.Object], NativeCameraCatalog.Selection(cam.Object), () => "Other", () => null);
        await guide.ConnectAsync(default);
        var frame = await guide.CaptureAsync(new CaptureRequest(100, Gain: 20, Subframe: new IntRect(1, 0, 2, 2)), default);
        frame.Pixels.Should().Equal(pixels);
        frame.Pixels.Should().NotBeSameAs(pixels);
        frame.BitsPerPixel.Should().Be(12);
        frame.Subframe.IsEmpty.Should().BeTrue();
        frame.ExposureMs.Should().Be(100);
        cam.VerifySet(c => c.Gain = 20);
        cam.VerifySet(c => c.EnableSubSample = false);
        cam.Verify(c => c.StartExposure(It.Is<CaptureSequence>(s => s.ExposureTime == 0.1 && s.Binning.X == 1)), Times.Once);
        await guide.DisconnectAsync();
        guide.IsConnected.Should().BeFalse();
    }

    [Test]
    public async Task Rejects_connected_imaging_camera_before_opening_sdk()
    {
        var cam = Camera();
        using var guide = new NativeGuideCamera(() => [cam.Object], NativeCameraCatalog.Selection(cam.Object), () => cam.Object.Id, () => cam.Object.Id);
        await FluentActions.Awaiting(() => guide.ConnectAsync(default)).Should().ThrowAsync<GuideCameraException>().WithMessage("*imaging camera*");
        cam.Verify(c => c.Connect(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Auto_selection_excludes_imaging_camera_and_reconnect_rediscovers()
    {
        var main = Camera("main");
        var cam = Camera("guide");
        int scans = 0;
        using var guide = new NativeGuideCamera(() => { scans++; return [main.Object, cam.Object]; }, "", () => "main", () => "main");
        await guide.ConnectAsync(default);
        await guide.ReconnectAsync(default);
        scans.Should().Be(2);
        cam.Verify(c => c.Connect(It.IsAny<CancellationToken>()), Times.Exactly(2));
        main.Verify(c => c.Connect(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Same_model_cameras_require_explicit_stable_device_selection()
    {
        var one = Camera("one");
        var two = Camera("two");
        using var ambiguous = new NativeGuideCamera(() => [one.Object, two.Object], "", () => null, () => null);
        await FluentActions.Awaiting(() => ambiguous.ConnectAsync(default)).Should().ThrowAsync<GuideCameraException>().WithMessage("*Several*");
        using var chosen = new NativeGuideCamera(() => [one.Object, two.Object], NativeCameraCatalog.Selection(two.Object), () => null, () => null);
        await chosen.ConnectAsync(default);
        two.Verify(c => c.Connect(It.IsAny<CancellationToken>()), Times.Once);
        one.Verify(c => c.Connect(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Timeout_and_cancellation_abort_pending_exposure(bool cancel)
    {
        var cam = Camera();
        using var cts = new CancellationTokenSource();
        cam.Setup(c => c.WaitUntilExposureIsReady(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => Task.Delay(Timeout.Infinite, ct));
        using var guide = new NativeGuideCamera(() => [cam.Object], cam.Object.Id, () => null, () => null, TimeSpan.FromMilliseconds(30));
        await guide.ConnectAsync(default);
        if (cancel) cts.CancelAfter(10);
        var capture = FluentActions.Awaiting(() => guide.CaptureAsync(new CaptureRequest(1), cts.Token));
        if (cancel) await capture.Should().ThrowAsync<OperationCanceledException>();
        else await capture.Should().ThrowAsync<GuideCameraException>().WithMessage("*timed out*");
        cam.Verify(c => c.AbortExposure(), Times.Once);
        await guide.DisconnectAsync();
    }

    [Test]
    public void Sdk_profile_does_not_share_or_persist_imaging_settings()
    {
        using var one = new GuideCameraProfileService();
        using var two = new GuideCameraProfileService();
        one.ActiveProfile.CameraSettings.Should().NotBeSameAs(two.ActiveProfile.CameraSettings);
        one.ActiveProfile.CameraSettings.BitScaling = true;
        two.ActiveProfile.CameraSettings.BitScaling.Should().BeFalse();
        NativeCameraCatalog.IsNative("SDK:SVBONY").Should().BeTrue();
        NativeCameraCatalog.IsNative("indi_svbony_ccd").Should().BeFalse();
    }
}
