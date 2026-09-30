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
        private readonly StreamWriter _allocations;
        private readonly StreamWriter _memory;
        private readonly StreamWriter _slowCalls;
        private readonly StreamWriter _memoryTrend;
        private readonly StreamWriter _modMemory;
        private readonly StreamWriter _modFields;
        private readonly StreamWriter _snapshots;
        private readonly StreamWriter _frameEvidence;
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
            _callbacks = Open("callback-spikes.csv", "elapsed_ms,frame,scene,frame_ms,scope,assembly,callback,calls,total_ms,max_ms,total_alloc_bytes,max_call_alloc_bytes,gc_crossing_calls,self_ms,no_observed_gc_ms,gc_overlap_ms,method_signature");
            _allocations = Open("callback-allocations.csv", "end_ms,interval_ms,frame,scene,scope,assembly,callback,calls,total_alloc_bytes,max_call_alloc_bytes,total_ms,gc_crossing_calls");
            _memory = Open("memory-operations.csv", "elapsed_ms,frame,scene,thread_id,operation,duration_ms,gc_gen0_delta,gc_gen1_delta,gc_gen2_delta,exception,caller_stack");
            _slowCalls = Open("slow-calls.csv", "elapsed_ms,frame,assembly,callback,duration_ms,alloc_bytes,gc_gen0_delta,gc_gen1_delta,gc_gen2_delta,exception,caller_stack,call_id,parent_call_id,parent_callback,depth,self_ms,timing_evidence,start_ms,method_signature");
            _memoryTrend = Open("memory-trend.csv", "elapsed_ms,utc,frame,scene,scene_visit,reason,mono_used_bytes,mono_reserved_bytes,unity_allocated_bytes,unity_reserved_bytes,working_set_bytes,private_committed_bytes,peak_working_set_bytes,process_sample_age_ms,process_counter_status,mono_delta_bytes,mono_growth_from_start_bytes,private_delta_bytes,private_growth_from_start_bytes,mono_change_since_previous_menu_bytes,private_change_since_previous_menu_bytes,gc_gen0,gc_gen1,gc_gen2,gc_gen0_delta,gc_gen1_delta,gc_gen2_delta");
            _modMemory = Open("mod-memory.csv", "sample_id,start_ms,end_ms,frame,scene,assembly,type,alive_observed_instances,alive_after_observed_gc,destroyed_unity_shells,alive_delta,reference_fields_inspected,reference_fields_omitted,field_read_failures,observation_attempts_dropped");
            _modFields = Open("mod-fields.csv", "sample_id,elapsed_ms,frame,scene,assembly,owner_type,observed_instance_id,first_observed_ms,alive_after_observed_gc,field,value_type,kind,count,count_delta,payload_bytes,native_object_bytes");
            _snapshots = Open("memory-snapshots.csv", "elapsed_ms,request_frame,request_scene,path,status,duration_ms,file_bytes,detail");
            _frameEvidence = Open("frame-evidence.csv", "elapsed_ms,frame,scene,frame_ms,root_callback_ms,measured_hook_ms,root_gc_overlap_calls,gc_observed,gc_marker_ms,depth_dropped,evidence");
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
        public void Allocation(string line) => Enqueue(5, line);
        public void Memory(string line) => Enqueue(6, line);
        public void SlowCall(string line) => Enqueue(7, line);
        public void MemoryTrend(string line) => Enqueue(8, line);
        public void ModMemory(string line) => Enqueue(9, line);
        public void ModField(string line) => Enqueue(10, line);
        public void Snapshot(string line) => Enqueue(11, line);
        public void FrameEvidence(string line) => Enqueue(12, line);

        private void Enqueue(byte kind, string line)
        {
            try
            {
                if (Failure != null || _queue.IsAddingCompleted || !_queue.TryAdd(new Entry(kind, line)))
                    Interlocked.Increment(ref _dropped);
            }
            catch (InvalidOperationException) { Interlocked.Increment(ref _dropped); }
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
                        case 4: _callbacks.WriteLine(entry.Line); break;
                        case 5: _allocations.WriteLine(entry.Line); break;
                        case 6: _memory.WriteLine(entry.Line); break;
                        case 7: _slowCalls.WriteLine(entry.Line); break;
                        case 8: _memoryTrend.WriteLine(entry.Line); break;
                        case 9: _modMemory.WriteLine(entry.Line); break;
                        case 10: _modFields.WriteLine(entry.Line); break;
                        case 11: _snapshots.WriteLine(entry.Line); break;
                        case 12: _frameEvidence.WriteLine(entry.Line); break;
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
                _allocations.Dispose();
                _memory.Dispose();
                _slowCalls.Dispose();
                _memoryTrend.Dispose();
                _modMemory.Dispose();
                _modFields.Dispose();
                _snapshots.Dispose();
                _frameEvidence.Dispose();
            }
        }

        private void Flush()
        {
            _frames.Flush();
            _incidents.Flush();
            _summaries.Flush();
            _events.Flush();
            _callbacks.Flush();
            _allocations.Flush();
            _memory.Flush();
            _slowCalls.Flush();
            _memoryTrend.Flush();
            _modMemory.Flush();
            _modFields.Flush();
            _snapshots.Flush();
            _frameEvidence.Flush();
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
