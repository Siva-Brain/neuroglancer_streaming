using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace BrainVolume.EditorTools
{
    /// <summary>
    /// Brain > IIP > Pre-download slide tiles: every tile of the IIP slide (IipSectionOverlay's settings: the one in
    /// the open scene, else its defaults) that shows tissue, at every level, into StreamingAssets/IIP/&lt;slide&gt;/,
    /// where the overlay reads them instead of the server (and builds ship them). Tissue = coloured pixels of the
    /// thumbnail (the white background is unsaturated), grown a little. Tiles already there are skipped, so it can be
    /// stopped (Cancel) and run again.
    /// </summary>
    public static class IipPredownload
    {
        const int Parallel = 8;
        const float TissueSaturation = 0.05f;   // between SlideSection's _SatLow / _SatHigh
        const int GrowThumbPx = 3;              // margin round the tissue (thumbnail px, 128 um each)

        static CancellationTokenSource _cts;
        static Task _task;
        static int _done, _total, _failed;
        static long _bytes;
        static string _dir;

        [MenuItem("Brain/IIP/Pre-download slide tiles (tissue only)")]
        public static void Run()
        {
            if (_task != null && !_task.IsCompleted) { Debug.Log("[IIP] Pre-download already running."); return; }

            var iip = Object.FindFirstObjectByType<BrainVolume.IipSectionOverlay>();
            GameObject temp = null;
            if (iip == null)
            {
                temp = new GameObject("IipPredownload") { hideFlags = HideFlags.HideAndDontSave };
                iip = temp.AddComponent<BrainVolume.IipSectionOverlay>();
            }
            _dir = iip.LocalDir(Application.streamingAssetsPath);
            int levels = iip.levels, baseSize = iip.baseSize, tileSize = iip.tileSize;
            var urls = new Dictionary<(int, int), string>();
            System.Func<int, int, string> url = (l, i) => urls.TryGetValue((l, i), out var u) ? u : urls[(l, i)] = iip.Url(l, i);
            url(0, 0);

            using (var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(60) })
            {
                // the thumbnail (level 0, one tile): the tissue mask
                string thumbPath = BrainVolume.IipSectionOverlay.TileFile(_dir, 0, 0);
                if (!File.Exists(thumbPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(thumbPath));
                    File.WriteAllBytes(thumbPath, http.GetByteArrayAsync(url(0, 0)).Result);
                }
                var thumb = new Texture2D(2, 2);
                thumb.LoadImage(File.ReadAllBytes(thumbPath));
                bool[,] tissue = TissueMask(thumb, out int tw, out int th);
                Object.DestroyImmediate(thumb);

                // summed-area table: is there tissue in a thumbnail rectangle?
                var sum = new int[th + 1, tw + 1];
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                        sum[y + 1, x + 1] = (tissue[y, x] ? 1 : 0) + sum[y, x + 1] + sum[y + 1, x] - sum[y, x];
                bool Any(float x0, float y0, float x1, float y1)
                {
                    int a = Mathf.Clamp(Mathf.FloorToInt(x0) - GrowThumbPx, 0, tw), b = Mathf.Clamp(Mathf.CeilToInt(x1) + GrowThumbPx, 0, tw);
                    int c = Mathf.Clamp(Mathf.FloorToInt(y0) - GrowThumbPx, 0, th), d = Mathf.Clamp(Mathf.CeilToInt(y1) + GrowThumbPx, 0, th);
                    return b > a && d > c && sum[d, b] - sum[c, b] - sum[d, a] + sum[c, a] > 0;
                }

                var jobs = new List<(int level, int index, string url, string path)>();
                int all = 0;
                for (int l = 0; l < levels; l++)
                {
                    int size = baseSize << l, cols = (size + tileSize - 1) / tileSize;
                    float f = 1 << l;   // level-l px per thumbnail px
                    for (int r = 0; r < cols; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            all++;
                            if (!Any(c * tileSize / f, r * tileSize / f, Mathf.Min((c + 1) * tileSize, size) / f, Mathf.Min((r + 1) * tileSize, size) / f)) continue;
                            int index = r * cols + c;
                            string path = BrainVolume.IipSectionOverlay.TileFile(_dir, l, index);
                            if (!File.Exists(path)) jobs.Add((l, index, url(l, index), path));
                        }
                }
                if (temp != null) Object.DestroyImmediate(temp);
                Debug.Log($"[IIP] Pre-download: {jobs.Count} tiles to fetch (tissue tiles not yet local; {all} in the pyramid) -> {_dir}");
                if (jobs.Count == 0) return;

                _done = 0; _failed = 0; _bytes = 0; _total = jobs.Count;
                _cts = new CancellationTokenSource();
                _task = Download(jobs, _cts.Token);
                EditorApplication.update += Progress;
            }
        }

        static async Task Download(List<(int level, int index, string url, string path)> jobs, CancellationToken token)
        {
            using (var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(120) })
            using (var gate = new SemaphoreSlim(Parallel))
            {
                var running = new List<Task>();
                foreach (var j in jobs)
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            byte[] data = await http.GetByteArrayAsync(j.url).ConfigureAwait(false);
                            Directory.CreateDirectory(Path.GetDirectoryName(j.path));
                            File.WriteAllBytes(j.path + ".part", data);
                            File.Move(j.path + ".part", j.path);
                            Interlocked.Add(ref _bytes, data.Length);
                        }
                        catch (System.Exception e)
                        {
                            Interlocked.Increment(ref _failed);
                            if (!token.IsCancellationRequested) Debug.LogWarning($"[IIP] tile {j.level},{j.index}: {e.Message}");
                        }
                        finally { Interlocked.Increment(ref _done); gate.Release(); }
                    }));
                }
                await Task.WhenAll(running).ConfigureAwait(false);
            }
        }

        static void Progress()
        {
            bool finished = _task == null || _task.IsCompleted;
            if (!finished && EditorUtility.DisplayCancelableProgressBar("Pre-downloading IIP slide tiles",
                    $"{_done} / {_total} tiles, {_bytes >> 20:N0} MB", _total > 0 ? (float)_done / _total : 0f))
                _cts.Cancel();
            if (!finished) return;
            EditorApplication.update -= Progress;
            EditorUtility.ClearProgressBar();
            bool cancelled = _cts != null && _cts.IsCancellationRequested;
            Debug.Log($"[IIP] Pre-download {(cancelled ? "cancelled" : "done")}: {_done - _failed} of {_total} tiles, " +
                      $"{_bytes >> 20:N0} MB, {_failed} failed -> {_dir}" + (_failed > 0 ? " (run it again for the rest)" : ""));
        }

        // Thumbnail pixels with colour (stained tissue), [y from the top, x]
        static bool[,] TissueMask(Texture2D t, out int w, out int h)
        {
            w = t.width; h = t.height;
            var px = t.GetPixels32();
            var m = new bool[h, w];
            for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[row * w + x];
                    int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    m[h - 1 - row, x] = max - min > TissueSaturation * 255f;   // texture rows run bottom-up
                }
            return m;
        }
    }
}
