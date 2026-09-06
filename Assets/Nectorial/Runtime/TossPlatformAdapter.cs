using System;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
using AppsInToss;
#endif

namespace Nectorial.SlideEscape.Unity
{
    internal enum TossStartupKind
    {
        WebFallback,
        NewUser,
        ExistingPayload,
        Blocked
    }

    internal readonly struct TossStartupResult
    {
        public readonly TossStartupKind Kind;
        public readonly string Payload;
        public readonly string Error;

        private TossStartupResult(TossStartupKind kind, string payload, string error)
        {
            Kind = kind;
            Payload = payload;
            Error = error;
        }

        public static TossStartupResult WebFallback()
        {
            return new TossStartupResult(TossStartupKind.WebFallback, null, null);
        }

        public static TossStartupResult NewUser()
        {
            return new TossStartupResult(TossStartupKind.NewUser, null, null);
        }

        public static TossStartupResult ExistingPayload(string payload)
        {
            return new TossStartupResult(TossStartupKind.ExistingPayload, payload, null);
        }

        public static TossStartupResult Blocked(string error)
        {
            return new TossStartupResult(TossStartupKind.Blocked, null, error);
        }
    }

    internal readonly struct TossPlatformOperationResult
    {
        public readonly bool Succeeded;
        public readonly string Error;

        private TossPlatformOperationResult(bool succeeded, string error)
        {
            Succeeded = succeeded;
            Error = error;
        }

        public static TossPlatformOperationResult Success()
        {
            return new TossPlatformOperationResult(true, null);
        }

        public static TossPlatformOperationResult Failure(string error)
        {
            return new TossPlatformOperationResult(false, error);
        }
    }

    public sealed class TossPlatformAdapter : MonoBehaviour
    {
        private const int PlatformCallTimeoutMilliseconds = 8000;

        private int _sessionGeneration;
        private bool _storageReady;
        private string _storageKey;

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
        private Action _unsubscribeBackEvent;
#endif

        public bool IsTossCandidate
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
                return true;
#else
                return false;
#endif
            }
        }

        public bool IsStorageReady => _storageReady;

        public event Action CheckpointRequested;

        internal void BeginStartup(Action<TossStartupResult> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
            _storageReady = false;
            _storageKey = null;
            int generation = ++_sessionGeneration;
            BeginTossStartupAsync(generation, completed);
#else
            completed(TossStartupResult.WebFallback());
#endif
        }

        internal void RetryStartup(Action<TossStartupResult> completed)
        {
            BeginStartup(completed);
        }

        internal void Store(int requestId, string payload, Action<int, TossPlatformOperationResult> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
            if (!_storageReady || string.IsNullOrEmpty(_storageKey))
            {
                completed(requestId, TossPlatformOperationResult.Failure("storage_not_ready"));
                return;
            }

            int generation = _sessionGeneration;
            StoreAsync(generation, requestId, payload, completed);
#else
            completed(requestId, TossPlatformOperationResult.Failure("not_toss_candidate"));
#endif
        }

        internal void Share(int requestId, string message, Action<int, TossPlatformOperationResult> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
            if (!_storageReady)
            {
                completed(requestId, TossPlatformOperationResult.Failure("share_not_ready"));
                return;
            }

            int generation = _sessionGeneration;
            ShareAsync(generation, requestId, message, completed);
#else
            completed(requestId, TossPlatformOperationResult.Failure("not_toss_candidate"));
#endif
        }

        private void OnDestroy()
        {
            _sessionGeneration++;
            _storageReady = false;
            _storageKey = null;

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
            if (_unsubscribeBackEvent != null)
            {
                _unsubscribeBackEvent();
                _unsubscribeBackEvent = null;
            }

            AITVisibilityHelper.OnVisibilityChanged -= OnVisibilityChanged;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR && APPSINTOS_OPTIMIZED
        private async void BeginTossStartupAsync(int generation, Action<TossStartupResult> completed)
        {
            try
            {
                GetAnonymousKeyResult identity = await AIT.GetAnonymousKey(PlatformCallTimeoutMilliseconds);
                if (!IsCurrent(generation)) return;

                GetAnonymousKeySuccessResponse success = identity != null && identity.IsSuccess
                    ? identity.GetSuccess()
                    : null;
                if (success == null || !string.Equals(success.Type, "HASH", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(success.Hash))
                {
                    CompleteStartup(generation, completed, TossStartupResult.Blocked("identity_invalid"));
                    return;
                }

                _storageKey = TossPlatformPolicy.BuildScopedStorageKey(success.Hash);
                string stored = await AIT.StorageGetItem(_storageKey, PlatformCallTimeoutMilliseconds);
                if (!IsCurrent(generation)) return;

                _storageReady = true;
                SubscribeLifecycleEvents();
                CompleteStartup(generation, completed, TossPlatformPolicy.IsMissingStorageValue(stored)
                    ? TossStartupResult.NewUser()
                    : TossStartupResult.ExistingPayload(stored));
            }
            catch (AITClientTimeoutException)
            {
                CompleteStartup(generation, completed, TossStartupResult.Blocked("startup_timeout"));
            }
            catch (AITException)
            {
                CompleteStartup(generation, completed, TossStartupResult.Blocked("startup_api_error"));
            }
            catch (Exception)
            {
                CompleteStartup(generation, completed, TossStartupResult.Blocked("startup_unavailable"));
            }
        }

        private async void StoreAsync(int generation, int requestId, string payload,
            Action<int, TossPlatformOperationResult> completed)
        {
            try
            {
                await AIT.StorageSetItem(_storageKey, payload);
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Success());
            }
            catch (AITException)
            {
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Failure("storage_api_error"));
            }
            catch (Exception)
            {
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Failure("storage_unavailable"));
            }
        }

        private async void ShareAsync(int generation, int requestId, string message,
            Action<int, TossPlatformOperationResult> completed)
        {
            try
            {
                await AIT.Share(new ShareMessage { Message = message }, PlatformCallTimeoutMilliseconds);
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Success());
            }
            catch (AITClientTimeoutException)
            {
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Failure("share_timeout"));
            }
            catch (AITException)
            {
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Failure("share_rejected"));
            }
            catch (Exception)
            {
                CompleteOperation(generation, requestId, completed, TossPlatformOperationResult.Failure("share_unavailable"));
            }
        }

        private void SubscribeLifecycleEvents()
        {
            if (_unsubscribeBackEvent == null)
            {
                _unsubscribeBackEvent = AIT.GraniteEventSubscribeBackEvent(RequestCheckpoint, IgnoreLifecycleError);
            }

            AITVisibilityHelper.OnVisibilityChanged -= OnVisibilityChanged;
            AITVisibilityHelper.OnVisibilityChanged += OnVisibilityChanged;
            if (!AITVisibilityHelper.IsVisible) RequestCheckpoint();
        }

        private void OnVisibilityChanged(bool isVisible)
        {
            if (!isVisible) RequestCheckpoint();
        }

        private void RequestCheckpoint()
        {
            if (_storageReady) CheckpointRequested?.Invoke();
        }

        private static void IgnoreLifecycleError(AITException exception)
        {
        }

        private bool IsCurrent(int generation)
        {
            return generation == _sessionGeneration;
        }

        private void CompleteStartup(int generation, Action<TossStartupResult> completed, TossStartupResult result)
        {
            if (IsCurrent(generation)) completed(result);
        }

        private void CompleteOperation(int generation, int requestId,
            Action<int, TossPlatformOperationResult> completed, TossPlatformOperationResult result)
        {
            if (IsCurrent(generation)) completed(requestId, result);
        }
#endif
    }
}
