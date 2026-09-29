using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BSProfiler
{
    internal sealed class CaptureWriter : IDisposable
    {
        private readonly BlockingCollection<Entry> _queue = new BlockingCollection<Entry>(4096);
        private readonly Thread _thread;
        private readonly StreamWriter _frames;
        private readonly StreamWriter _incidents;
        private readonly StreamWriter _summaries;
        private readonly StreamWriter _events;
        private readonly StreamWriter _callbacks;
        private int _dropped;

        public string DirectoryPath { get; }
        public Exception? Failure { get; private set; }
        public int DroppedRecords => Volatile.Read(ref _dropped);

        public CaptureWriter(string directory, string frameHeader)
        {
            DirectoryPath = directory;
            Directory.CreateDirectory(directory);
            _frames = Open("frames.csv", frameHeader);
            _incidents = Open("incidents.csv", "start_ms,end_ms,scene,slow_frames,peak_frame,peak_frame_ms,peak_cpu_main_ms,peak_gpu_span_ms,peak_gc_alloc_bytes,threshold_ms");
            _summaries = Open("summaries.csv", "end_ms,scene,frames,mean_ms,p50_ms,p95_ms,p99_ms,worst_ms,slow_frames,mean_cpu_main_ms,mean_gpu_span_ms,gc_gen0,gc_gen1,gc_gen2,queue_dropped");
            _events = Open("events.csv", "elapsed_ms,kind,detail");
            _callbacks = Open("callback-spikes.csv", "elapsed_ms,frame,scene,frame_ms,scope,assembly,callback,calls,total_ms,max_ms");
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "BSProfiler writer" };
            _thread.Start();
        }

        private StreamWriter Open(string name, string header)
        {
            var writer = new StreamWriter(Path.Combine(DirectoryPath, name), false, new UTF8Encoding(false), 65536);
            writer.WriteLine(header);
            return writer;
        }

        public void Frame(string line) => Enqueue(0, line);
        public void Incident(string line) => Enqueue(1, line);
        public void Summary(string line) => Enqueue(2, line);
        public void Event(string line) => Enqueue(3, line);
        public void Callback(string line) => Enqueue(4, line);

        private void Enqueue(byte kind, string line)
        {
            if (Failure != null || _queue.IsAddingCompleted || !_queue.TryAdd(new Entry(kind, line)))
                Interlocked.Increment(ref _dropped);
        }

        private void WriteLoop()
        {
            try
            {
                long lastFlush = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (Entry entry in _queue.GetConsumingEnumerable())
                {
                    switch (entry.Kind)
                    {
                        case 0: _frames.WriteLine(entry.Line); break;
                        case 1: _incidents.WriteLine(entry.Line); break;
                        case 2: _summaries.WriteLine(entry.Line); break;
                        case 3: _events.WriteLine(entry.Line); break;
                        default: _callbacks.WriteLine(entry.Line); break;
                    }

                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if ((now - lastFlush) / (double)System.Diagnostics.Stopwatch.Frequency >= 5)
                    {
                        Flush();
                        lastFlush = now;
                    }
                }
                Flush();
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
            finally
            {
                _frames.Dispose();
                _incidents.Dispose();
                _summaries.Dispose();
                _events.Dispose();
                _callbacks.Dispose();
            }
        }

        private void Flush()
        {
            _frames.Flush();
            _incidents.Flush();
            _summaries.Flush();
            _events.Flush();
            _callbacks.Flush();
        }

        public void Dispose()
        {
            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();
            _thread.Join();
        }

        public static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? ""
            : value.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return '"' + value!.Replace("\"", "\"\"") + '"';
        }

        private readonly struct Entry
        {
            public readonly byte Kind;
            public readonly string Line;
            public Entry(byte kind, string line) { Kind = kind; Line = line; }
        }
    }
}
