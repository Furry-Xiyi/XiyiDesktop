using System;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Input.Interop;
using Windows.Devices.Haptics;
using Windows.Foundation.Metadata;

namespace XiyiDesktop.DesktopItems;

internal enum PenHapticAction
{
    Click,
    Error,
    Hover,
    Press,
    Release,
    Success,
    BuzzContinuous,
    RumbleContinuous,
    LongInteraction
}

internal static class PenHapticsFeedback
{
    private static readonly TimeSpan LongInteractionDuration = TimeSpan.FromMilliseconds(450);

    public static void Send(PointerPoint pointerPoint, PenHapticAction action)
    {
        try
        {
            if (!ApiInformation.IsPropertyPresent(
                    "Windows.Devices.Input.PenDevice",
                    "SimpleHapticsController"))
                return;

            var controller = PenDeviceInterop.FromPointerPoint(pointerPoint)?.SimpleHapticsController;
            if (controller is null)
                return;

            if (action == PenHapticAction.LongInteraction)
            {
                SendLongInteraction(controller);
                return;
            }

            var requestedFeedback = FindFeedback(controller, GetWaveform(action));
            var feedback = requestedFeedback ?? FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.Click);
            if (feedback is not null)
                SendFeedback(controller, feedback);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not send pen haptic feedback: {exception.Message}");
        }
    }

    private static void SendLongInteraction(SimpleHapticsController controller)
    {
        var feedback = FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.BuzzContinuous) ??
            FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.RumbleContinuous) ??
            FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.Press) ??
            FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.Click);
        if (feedback is null)
            return;

        if (controller.IsPlayDurationSupported)
        {
            controller.SendHapticFeedbackForDuration(
                feedback,
                controller.IsIntensitySupported ? 0.75 : 1.0,
                LongInteractionDuration);
            return;
        }

        SendFeedback(controller, FindFeedback(controller, KnownSimpleHapticsControllerWaveforms.Click) ?? feedback);
    }

    private static void SendFeedback(SimpleHapticsController controller, SimpleHapticsControllerFeedback feedback)
    {
        if (controller.IsIntensitySupported)
            controller.SendHapticFeedback(feedback, 0.75);
        else
            controller.SendHapticFeedback(feedback);
    }

    private static SimpleHapticsControllerFeedback? FindFeedback(
        SimpleHapticsController controller,
        ushort waveform) => controller.SupportedFeedback.FirstOrDefault(candidate => candidate.Waveform == waveform);

    private static ushort GetWaveform(PenHapticAction action) => action switch
    {
        PenHapticAction.Error => KnownSimpleHapticsControllerWaveforms.Error,
        PenHapticAction.Hover => KnownSimpleHapticsControllerWaveforms.Hover,
        PenHapticAction.Press => KnownSimpleHapticsControllerWaveforms.Press,
        PenHapticAction.Release => KnownSimpleHapticsControllerWaveforms.Release,
        PenHapticAction.Success => KnownSimpleHapticsControllerWaveforms.Success,
        PenHapticAction.BuzzContinuous => KnownSimpleHapticsControllerWaveforms.BuzzContinuous,
        PenHapticAction.RumbleContinuous => KnownSimpleHapticsControllerWaveforms.RumbleContinuous,
        _ => KnownSimpleHapticsControllerWaveforms.Click
    };
}
