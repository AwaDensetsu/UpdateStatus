using System;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public enum EpicConnectionFailure { None, PermissionDenied, DiscoveryFailed, TimedOut, ConnectionUnavailable, InterfaceUnavailable, InvalidResponse, UnexpectedError }
    internal sealed class EpicConnectionException : IOException
    {
        internal EpicConnectionFailure Failure { get; }
        internal EpicConnectionException(EpicConnectionFailure failure) : base(EpicConnectionFailures.Message(failure)) { Failure = failure; }
    }
    internal static class EpicConnectionFailures
    {
        internal static EpicConnectionFailure Classify(Exception error, bool discovering)
        {
            var explicitFailure = error as EpicConnectionException;
            if (explicitFailure != null) return explicitFailure.Failure;
            if (error is UnauthorizedAccessException || error is SecurityException || error is Win32Exception && ((Win32Exception)error).NativeErrorCode == 5)
                return EpicConnectionFailure.PermissionDenied;
            if (error is OperationCanceledException || error is TimeoutException) return EpicConnectionFailure.TimedOut;
            if (discovering || error is ManagementException) return EpicConnectionFailure.DiscoveryFailed;
            if (error is HttpRequestException || error is WebSocketException) return EpicConnectionFailure.ConnectionUnavailable;
            if (error is InvalidDataException || error is FormatException || error is ArgumentException) return EpicConnectionFailure.InvalidResponse;
            return EpicConnectionFailure.UnexpectedError;
        }
        internal static string Message(EpicConnectionFailure failure)
        {
            switch (failure)
            {
                case EpicConnectionFailure.PermissionDenied: return "Epic connection access was denied. Check launcher and Windows permissions.";
                case EpicConnectionFailure.DiscoveryFailed: return "Could not inspect the Epic process. Check Windows process services and permissions, then try again.";
                case EpicConnectionFailure.TimedOut: return "Epic did not respond in time. Wait for it to finish opening; the plugin will retry.";
                case EpicConnectionFailure.ConnectionUnavailable: return "The Epic connection is unavailable. Check that Epic is running with the saved connection setup.";
                case EpicConnectionFailure.InterfaceUnavailable: return "This Epic UI interface is unavailable. Check for a compatible Update Status version.";
                case EpicConnectionFailure.InvalidResponse: return "Epic returned unsupported or invalid update data. The plugin will retry; check compatibility if this persists.";
                case EpicConnectionFailure.UnexpectedError: return "Epic observation encountered an error. The plugin will retry; report it if the problem persists.";
                default: return null;
            }
        }
    }
}
