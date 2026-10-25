// Shared decoded SOG assets for streamed copies of the same tile.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Deduplicates in-flight and resident runtime assets when several LOD streamers reference
    /// the same URI. Renderer-specific sort buffers remain per renderer; the immutable splat
    /// attributes and their uploaded resource are shared through GsplatResourceManager.
    /// </summary>
    internal static class GsplatLodSharedAssetCache
    {
        sealed class Entry
        {
            public string Key;
            public int References;
            public bool Loading = true;
            public GsplatAsset Asset;
            public readonly CancellationTokenSource Lifetime = new();
            public readonly TaskCompletionSource<GsplatAsset> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Entry()
            {
                _ = Completion.Task.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        sealed class AssetReferenceComparer : IEqualityComparer<GsplatAsset>
        {
            public bool Equals(GsplatAsset a, GsplatAsset b) => ReferenceEquals(a, b);
            public int GetHashCode(GsplatAsset asset) => RuntimeHelpers.GetHashCode(asset);
        }

        static readonly object s_lock = new();
        static readonly Dictionary<string, Entry> s_byKey = new(StringComparer.Ordinal);
        static readonly Dictionary<GsplatAsset, Entry> s_byAsset =
            new(new AssetReferenceComparer());
        static int s_loadCount;
        static int s_joinCount;
        static int s_reuseCount;

        public static int LoadCount { get { lock (s_lock) return s_loadCount; } }
        public static int JoinedLoadCount { get { lock (s_lock) return s_joinCount; } }
        public static int ReuseCount { get { lock (s_lock) return s_reuseCount; } }

        public static void ResetCounters()
        {
            lock (s_lock)
            {
                s_loadCount = 0;
                s_joinCount = 0;
                s_reuseCount = 0;
            }
        }

        public static string MakeKey(string uri, CompressionMode compression,
            SourceCoordinates coordinates) =>
            $"{(int)compression}:{(int)coordinates}:{uri.Replace('\\', '/') }";

        public static async Task<GsplatAsset> AcquireAsync(string key, CancellationToken token,
            Func<CancellationToken, Task<GsplatAsset>> loader)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Cache key is empty.", nameof(key));
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            token.ThrowIfCancellationRequested();

            Entry entry;
            bool startLoad = false;
            GsplatAsset readyAsset = null;
            lock (s_lock)
            {
                if (s_byKey.TryGetValue(key, out entry))
                {
                    ++entry.References;
                    if (entry.Asset)
                    {
                        readyAsset = entry.Asset;
                        ++s_reuseCount;
                    }
                    else
                    {
                        ++s_joinCount;
                    }
                }
                else
                {
                    entry = new Entry { Key = key, References = 1 };
                    s_byKey.Add(key, entry);
                    ++s_loadCount;
                    startLoad = true;
                }
            }

            if (readyAsset) return readyAsset;
            if (startLoad) _ = LoadEntryAsync(entry, loader);

            try
            {
                return await AwaitWithCancellation(entry.Completion.Task, token);
            }
            catch
            {
                GsplatAsset unused = ReleaseReference(entry);
                DestroyUnityAsset(unused);
                throw;
            }
        }

        public static bool Release(GsplatAsset asset)
        {
            if (ReferenceEquals(asset, null)) return false;
            Entry entry;
            lock (s_lock)
            {
                if (!s_byAsset.TryGetValue(asset, out entry)) return false;
            }

            DestroyUnityAsset(ReleaseReference(entry));
            return true;
        }

        static async Task LoadEntryAsync(Entry entry,
            Func<CancellationToken, Task<GsplatAsset>> loader)
        {
            GsplatAsset loaded = null;
            try
            {
                loaded = await loader(entry.Lifetime.Token);
                if (!loaded) throw new InvalidOperationException("The shared LOD asset loader returned null.");

                bool retained;
                lock (s_lock)
                {
                    entry.Loading = false;
                    retained = entry.References > 0 &&
                               s_byKey.TryGetValue(entry.Key, out Entry current) &&
                               ReferenceEquals(current, entry);
                    if (retained)
                    {
                        entry.Asset = loaded;
                        s_byAsset.Add(loaded, entry);
                    }
                    else if (s_byKey.TryGetValue(entry.Key, out Entry existing) && ReferenceEquals(existing, entry))
                    {
                        s_byKey.Remove(entry.Key);
                    }
                }

                if (retained)
                {
                    entry.Completion.TrySetResult(loaded);
                    loaded = null;
                }
                else
                {
                    entry.Completion.TrySetCanceled();
                }
            }
            catch (OperationCanceledException)
            {
                RemoveEntry(entry);
                entry.Completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                bool hasWaiters;
                lock (s_lock)
                {
                    hasWaiters = entry.References > 0;
                    entry.Loading = false;
                    if (s_byKey.TryGetValue(entry.Key, out Entry current) && ReferenceEquals(current, entry))
                        s_byKey.Remove(entry.Key);
                }

                if (hasWaiters) entry.Completion.TrySetException(exception);
                else entry.Completion.TrySetCanceled();
            }
            finally
            {
                DestroyUnityAsset(loaded);
                entry.Lifetime.Dispose();
            }
        }

        static GsplatAsset ReleaseReference(Entry entry)
        {
            CancellationTokenSource cancel = null;
            GsplatAsset destroy = null;
            lock (s_lock)
            {
                if (entry.References > 0) --entry.References;
                if (entry.References != 0) return null;

                if (entry.Asset)
                {
                    destroy = entry.Asset;
                    s_byAsset.Remove(entry.Asset);
                    entry.Asset = null;
                }
                else if (entry.Loading)
                {
                    cancel = entry.Lifetime;
                }

                if (s_byKey.TryGetValue(entry.Key, out Entry current) && ReferenceEquals(current, entry))
                    s_byKey.Remove(entry.Key);
            }

            if (cancel != null)
            {
                try { cancel.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            return destroy;
        }

        static void RemoveEntry(Entry entry)
        {
            lock (s_lock)
            {
                entry.Loading = false;
                if (s_byKey.TryGetValue(entry.Key, out Entry current) && ReferenceEquals(current, entry))
                    s_byKey.Remove(entry.Key);
            }
        }

        static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken token)
        {
            if (!token.CanBeCanceled) return await task;
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), canceled))
            {
                if (await Task.WhenAny(task, canceled.Task) != task)
                    throw new OperationCanceledException(token);
            }
            token.ThrowIfCancellationRequested();
            return await task;
        }

        static void DestroyUnityAsset(GsplatAsset asset)
        {
            if (!asset) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(asset);
            else UnityEngine.Object.DestroyImmediate(asset);
        }
    }
}
