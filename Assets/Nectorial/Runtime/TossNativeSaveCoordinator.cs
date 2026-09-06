namespace Nectorial.SlideEscape.Unity
{
    internal readonly struct TossNativeSaveCompletion
    {
        public readonly TossWriteRequest Request;
        public readonly bool AffectsCurrentStatus;

        public TossNativeSaveCompletion(TossWriteRequest request, bool affectsCurrentStatus)
        {
            Request = request;
            AffectsCurrentStatus = affectsCurrentStatus;
        }
    }

    // Owns the user-visible save state as well as the serial writer. A UI
    // timeout may release input, but never releases the active native write.
    internal sealed class TossNativeSaveCoordinator
    {
        private readonly TossSerialWritePolicy _writer = new TossSerialWritePolicy();
        private int _latestRequestId;
        private int _pendingManualRequestId;

        internal bool ManualPending => _pendingManualRequestId != 0;
        internal int PendingManualRequestId => _pendingManualRequestId;
        internal string Status { get; private set; } = "idle";
        internal string Error { get; private set; } = string.Empty;

        internal bool Queue(TossWriteRequest request, out TossWriteRequest requestToStart)
        {
            _latestRequestId = request.Id;
            if (request.IsManual)
            {
                _pendingManualRequestId = request.Id;
                Status = "pending";
                Error = string.Empty;
            }
            else if (!ManualPending)
            {
                Status = "changed";
                Error = string.Empty;
            }

            return _writer.Enqueue(request, out requestToStart);
        }

        internal bool TimeoutManual(int requestId)
        {
            if (_pendingManualRequestId != requestId) return false;

            _pendingManualRequestId = 0;
            Status = "failed";
            Error = "storage_ui_timeout";
            return true;
        }

        internal bool Complete(int requestId, bool succeeded, string error,
            out TossNativeSaveCompletion completion, out TossWriteRequest nextToStart)
        {
            TossWriteRequest completed;
            if (!_writer.Complete(requestId, out completed, out nextToStart))
            {
                completion = default(TossNativeSaveCompletion);
                return false;
            }

            if (completed.IsManual && _pendingManualRequestId == requestId)
            {
                _pendingManualRequestId = 0;
            }

            bool affectsCurrentStatus = requestId == _latestRequestId;
            if (affectsCurrentStatus)
            {
                Status = succeeded ? "saved" : "failed";
                Error = succeeded ? string.Empty : (error ?? "storage_unknown");
            }

            completion = new TossNativeSaveCompletion(completed, affectsCurrentStatus);
            return true;
        }
    }
}
