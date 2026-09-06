namespace Nectorial.SlideEscape.Unity
{
    internal readonly struct TossWriteRequest
    {
        public readonly int Id;
        public readonly string Payload;
        public readonly bool IsManual;

        public TossWriteRequest(int id, string payload, bool isManual)
        {
            Id = id;
            Payload = payload;
            IsManual = isManual;
        }
    }

    // StorageSetItem client timeouts do not cancel the native operation. This
    // policy therefore never frees the active slot until that operation has
    // actually completed; at most one newer checkpoint is retained behind it.
    internal sealed class TossSerialWritePolicy
    {
        private bool _hasActive;
        private TossWriteRequest _active;
        private bool _hasQueued;
        private TossWriteRequest _queued;

        internal bool Enqueue(TossWriteRequest request, out TossWriteRequest requestToStart)
        {
            if (!_hasActive)
            {
                _active = request;
                _hasActive = true;
                requestToStart = request;
                return true;
            }

            _queued = request;
            _hasQueued = true;
            requestToStart = default(TossWriteRequest);
            return false;
        }

        internal bool Complete(int requestId, out TossWriteRequest completed, out TossWriteRequest nextToStart)
        {
            if (!_hasActive || _active.Id != requestId)
            {
                completed = default(TossWriteRequest);
                nextToStart = default(TossWriteRequest);
                return false;
            }

            completed = _active;
            if (_hasQueued)
            {
                _active = _queued;
                _hasQueued = false;
                _queued = default(TossWriteRequest);
                nextToStart = _active;
                return true;
            }

            _hasActive = false;
            _active = default(TossWriteRequest);
            nextToStart = default(TossWriteRequest);
            return true;
        }
    }
}
