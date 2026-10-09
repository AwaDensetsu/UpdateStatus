namespace PlayniteUpdateStatus.Core
{
    public enum UpdateCommand { Pause, Resume, Close }
    // Optional capability. Poll remains observation-only; commands require explicit UI action.
    public interface IUpdateControlSession : IUpdateSession
    {
        bool SendCommand(UpdateCommand command);
    }
    // Close is separate from Pause: providers confirm that the selected update
    // no longer belongs to their download queue before the UI is dismissed.
    public interface IUpdateCloseSession : IUpdateControlSession
    {
        bool SendClose();
    }
    // Nonblocking: disarm automatic preparation/selection while retaining the
    // getters needed to confirm an explicit graceful-exit command.
    public interface IUpdateExitSession
    {
        void PrepareForExit();
    }
}
