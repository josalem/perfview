using FastSerialization;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Microsoft.Diagnostics.Tracing
{
    internal static class EPESInstrumentationSource
    {
        private static FileStream logStream = null;
        private static StreamWriter writer = null;
        private static Stopwatch sw = null;
        private static GZipStream zipStream = null;
        private static object writeSync = new object();
        private static object configSync = new object();
        private static bool isEnabled = false;
        private static Timer timer = null;
        private static TimeSpan rolloverDuration = TimeSpan.FromMinutes(30);
        private static Queue<string> previousLogs = new Queue<string>();

        private static void StartNewLog()
        {
            lock (configSync)
            {
                if (!isEnabled)
                    return;

                string newFileName = $"EPES_log_{Process.GetCurrentProcess().Id}_{DateTime.Now:yyyyMMddHHmmss}.txt.gz";
                var newFileStream = new FileStream(newFileName, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 256 * (1 << 10) /* 256 KB */);
                var newZipStream = new GZipStream(newFileStream, CompressionLevel.Fastest);
                var newStreamWriter = new StreamWriter(newZipStream);

                FileStream tmpFileStream = null;
                GZipStream tmpZipStream = null;
                StreamWriter tmpStreamWriter = null;

                // swap out the logs
                lock (writeSync)
                {
                    tmpFileStream = logStream;
                    tmpZipStream = zipStream;
                    tmpStreamWriter = writer;

                    logStream = newFileStream;
                    zipStream = newZipStream;
                    writer = newStreamWriter;
                }

                tmpStreamWriter?.Dispose();
                tmpZipStream?.Dispose();
                tmpFileStream?.Dispose();

                previousLogs.Enqueue(newFileName);

                // only keep the last 2 logs
                if (previousLogs.Count > 2)
                {
                    File.Delete(previousLogs.Dequeue());
                }
            }
        }

        public static void Init()
        {
            lock (configSync)
            {
                isEnabled = true;
                // Set to an int to specify a number of minutes for file rollover.  Any other value will use the default (30 minutes)
                string enabledValue = Environment.GetEnvironmentVariable("TRACE_EVENT_ENABLE_INSTRUMENTATION");
                if (string.IsNullOrEmpty(enabledValue))
                    return;
                else if (int.TryParse(enabledValue, out int nMinutes) && nMinutes > 0)
                    rolloverDuration = TimeSpan.FromMinutes(nMinutes);
                sw = new Stopwatch();
                sw.Start();
                timer = new Timer(_ =>
                {
                    StartNewLog();
                }, null, TimeSpan.FromMilliseconds(0), rolloverDuration);
            }
        }

        public static void Finish()
        {
            lock (writeSync)
            {
                lock (configSync)
                {
                    isEnabled = false;
                    timer?.Dispose();
                    timer = null;
                    writer?.Dispose();
                    writer = null;
                    zipStream?.Dispose();
                    zipStream = null;
                    logStream?.Dispose();
                    logStream = null;
                    sw?.Stop();
                }
            }
        }

        public static void StartReadFromSocket(long length) { lock (writeSync) { writer?.WriteLine($"{sw.Elapsed.TotalSeconds:F9};R;0;{length}"); } }
        public static void StopReadFromSocket(long length) { lock (writeSync) { writer?.WriteLine($"{sw.Elapsed.TotalSeconds:F9};R;1;{length}"); } }
        public static void StartDispatchEvent() { lock (writeSync) { writer?.WriteLine($"{sw.Elapsed.TotalSeconds:F9};D;0"); } }
        public static void StopDispatchEvent() { lock (writeSync) { writer?.WriteLine($"{sw.Elapsed.TotalSeconds:F9};D;1"); } }
    }

    // This Stream implementation takes one stream
    // and proxies the Stream API
    internal class StreamProxy : Stream
    {
        private Stream ProxiedStream { get; }
        public override bool CanRead => ProxiedStream.CanRead;

        public override bool CanSeek => ProxiedStream.CanSeek;

        public override bool CanWrite => ProxiedStream.CanWrite;

        public override long Length => ProxiedStream.Length;

        public override long Position { get => ProxiedStream.Position; set => ProxiedStream.Position = value; }

        public StreamProxy(Stream streamToProxy)
        {
            ProxiedStream = streamToProxy;
        }

        public override void Flush() => ProxiedStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            EPESInstrumentationSource.StartReadFromSocket(count);
            var readCount = ProxiedStream.Read(buffer, offset, count);
            EPESInstrumentationSource.StopReadFromSocket(readCount);
            return readCount;
        }

        public override long Seek(long offset, SeekOrigin origin) => ProxiedStream.Seek(offset, origin);

        public override void SetLength(long value) => ProxiedStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            // This stream is only for "reading" from. No need for this method.
            throw new System.NotImplementedException();
        }

        protected bool disposed = false;
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    ProxiedStream.Dispose();
                }

                disposed = true;
            }
        }
    }



    /// <summary>
    /// EventPipeEventSource knows how to decode EventPipe (generated by the .NET core runtime).
    /// Please see <see href="https://github.com/Microsoft/perfview/blob/master/src/TraceEvent/EventPipe/EventPipeFormat.md" />for details on the file format.
    ///
    /// By conventions files of such a format are given the .netperf suffix and are logically
    /// very much like a ETL file in that they have a header that indicate things about
    /// the trace as a whole, and a list of events.    Like more modern ETL files the
    /// file as a whole is self-describing.    Some of the events are 'MetaData' events
    /// that indicate the provider name, event name, and payload field names and types.
    /// Ordinary events then point at these meta-data event so that logically all
    /// events have a name some basic information (process, thread, timestamp, activity
    /// ID) and user defined field names and values of various types.
    /// </summary>
    public unsafe class EventPipeEventSource : TraceEventDispatcher, IFastSerializable, IFastSerializableVersion
    {
        public EventPipeEventSource(string fileName) : this(new PinnedStreamReader(fileName, 0x20000), fileName)
        {
        }

        public EventPipeEventSource(Stream stream) : this(new PinnedStreamReader(new StreamProxy(stream)), "stream")
        {
        }

        private EventPipeEventSource(PinnedStreamReader streamReader, string name)
        {
            EPESInstrumentationSource.Init();
            StreamLabel start = streamReader.Current;
            byte[] netTraceMagic = new byte[8];
            streamReader.Read(netTraceMagic, 0, netTraceMagic.Length);
            byte[] expectedMagic = Encoding.UTF8.GetBytes("Nettrace");
            bool isNetTrace = true;
            if (!netTraceMagic.SequenceEqual(expectedMagic))
            {
                // The older netperf format didn't have this 'Nettrace' magic on it.
                streamReader.Goto(start);
                isNetTrace = false;
            }

            osVersion = new Version("0.0.0.0");
            cpuSpeedMHz = 10;

            _deserializer = new Deserializer(streamReader, name);

#if SUPPORT_V1_V2
            // This is only here for V2 and V1.  V3+ should use the name EventTrace, it can be removed when we drop support.
            _deserializer.RegisterFactory("Microsoft.DotNet.Runtime.EventPipeFile", delegate { return this; });
#endif
            _deserializer.RegisterFactory("Trace", delegate { return this; });
            _deserializer.RegisterFactory("EventBlock", delegate { return new EventPipeEventBlock(this); });
            _deserializer.RegisterFactory("MetadataBlock", delegate { return new EventPipeMetadataBlock(this); });
            _deserializer.RegisterFactory("SPBlock", delegate { return new EventPipeSequencePointBlock(this); });
            _deserializer.RegisterFactory("StackBlock", delegate { return new EventPipeStackBlock(this); });

            var entryObj = _deserializer.GetEntryObject(); // this call invokes FromStream and reads header data

            if((FileFormatVersionNumber >= 4) != isNetTrace)
            {
                //NetTrace header should be present iff the version is >= 4
                throw new SerializationException("Invalid NetTrace file format version");
            }

            // Because we told the deserialize to use 'this' when creating a EventPipeFile, we
            // expect the entry object to be 'this'.
            Debug.Assert(entryObj == this);

            EventCache = new EventCache();
            EventCache.OnEvent += EventCache_OnEvent;
            EventCache.OnEventsDropped += EventCache_OnEventsDropped;
            StackCache = new StackCache();
        }

        #region private
        // I put these in the private section because they are overrides, and thus don't ADD to the API.
        public override int EventsLost => _eventsLost;

        /// <summary>
        /// This is the version number reader and writer (although we don't don't have a writer at the moment)
        /// It MUST be updated (as well as MinimumReaderVersion), if breaking changes have been made.
        /// If your changes are forward compatible (old readers can still read the new format) you
        /// don't have to update the version number but it is useful to do so (while keeping MinimumReaderVersion unchanged)
        /// so that readers can quickly determine what new content is available.
        /// </summary>
        public int Version => 5;

        /// <summary>
        /// This field is only used for writers, and this code does not have writers so it is not used.
        /// It should be set to Version unless changes since the last version are forward compatible
        /// (old readers can still read this format), in which case this should be unchanged.
        /// </summary>
        public int MinimumReaderVersion => Version;

        /// <summary>
        /// This is the smallest version that the deserializer here can read.   Currently
        /// we are careful about backward compat so our deserializer can read anything that
        /// has ever been produced.   We may change this when we believe old writers basically
        /// no longer exist (and we can remove that support code).
        /// </summary>
        public int MinimumVersionCanRead => 0;

        protected override void Dispose(bool disposing)
        {
            _deserializer.Dispose();

            base.Dispose(disposing);
        }

        public override bool Process()
        {
            if (FileFormatVersionNumber >= 3)
            {
                // loop through the stream until we hit a null object.  Deserialization of
                // EventPipeEventBlocks will cause dispatch to happen.
                // ReadObject uses registered factories and recognizes types by names, then derserializes them with FromStream
                while (_deserializer.ReadObject() != null)
                { }

                if(FileFormatVersionNumber >= 4)
                {
                    // Ensure all events have been sorted and dispatched
                    EventCache.Flush();
                }
            }
#if SUPPORT_V1_V2
            else
            {
                Stopwatch sw = new Stopwatch();
                PinnedStreamReader deserializerReader = (PinnedStreamReader)_deserializer.Reader;
                while (deserializerReader.Current < _endOfEventStream)
                {
                    TraceEventNativeMethods.EVENT_RECORD* eventRecord = ReadEvent(deserializerReader, false);
                    if (eventRecord != null)
                    {
                        // in the code below we set sessionEndTimeQPC to be the timestamp of the last event.
                        // Thus the new timestamp should be later, and not more than 1 day later.
                        Debug.Assert(sessionEndTimeQPC <= eventRecord->EventHeader.TimeStamp);
                        Debug.Assert(sessionEndTimeQPC == 0 || eventRecord->EventHeader.TimeStamp - sessionEndTimeQPC < _QPCFreq * 24 * 3600);

                        var traceEvent = Lookup(eventRecord);
                        Dispatch(traceEvent);
                        sessionEndTimeQPC = eventRecord->EventHeader.TimeStamp;
                    }
                }
            }
#endif
            EPESInstrumentationSource.Finish();
            return true;
        }

        internal int FileFormatVersionNumber { get; private set; }
        internal EventCache EventCache { get; private set; }
        internal StackCache StackCache { get; private set; }

        internal override string ProcessName(int processID, long timeQPC) => string.Format("Process({0})", processID);

        internal void ReadAndDispatchEvent(PinnedStreamReader reader, bool useHeaderCompression)
        {
            DispatchEventRecord(ReadEvent(reader, useHeaderCompression));
        }

        internal void DispatchEventRecord(TraceEventNativeMethods.EVENT_RECORD* eventRecord)
        {
            if (eventRecord != null)
            {
                EPESInstrumentationSource.StartDispatchEvent();
                // in the code below we set sessionEndTimeQPC to be the timestamp of the last event.
                // Thus the new timestamp should be later, and not more than 1 day later.
                Debug.Assert(sessionEndTimeQPC <= eventRecord->EventHeader.TimeStamp);
                Debug.Assert(sessionEndTimeQPC == 0 || eventRecord->EventHeader.TimeStamp - sessionEndTimeQPC < _QPCFreq * 24 * 3600);

                var traceEvent = Lookup(eventRecord);
                Dispatch(traceEvent);
                sessionEndTimeQPC = eventRecord->EventHeader.TimeStamp;
                EPESInstrumentationSource.StopDispatchEvent();
            }
        }

        internal void ResetCompressedHeader()
        {
            _compressedHeader = new EventPipeEventHeader();
        }

        internal TraceEventNativeMethods.EVENT_RECORD* ReadEvent(PinnedStreamReader reader, bool useHeaderCompression)
        {
            byte* headerPtr = null;

            if (useHeaderCompression)
            {
                // The header uses a variable size encoding, but it is certainly smaller than 100 bytes
                const int maxHeaderSize = 100;
                headerPtr = reader.GetPointer(maxHeaderSize);
                ReadEventHeader(headerPtr, useHeaderCompression, ref _compressedHeader);
                return ReadEvent(_compressedHeader, reader);
            }
            else
            {
                headerPtr = reader.GetPointer(EventPipeEventHeader.GetHeaderSize(FileFormatVersionNumber));
                int totalSize = EventPipeEventHeader.GetTotalEventSize(headerPtr, FileFormatVersionNumber);
                headerPtr = reader.GetPointer(totalSize); // now we now the real size and get read entire event
                EventPipeEventHeader eventData = new EventPipeEventHeader();
                ReadEventHeader(headerPtr, useHeaderCompression, ref eventData);
                return ReadEvent(eventData, reader);
            }
        }

        void ReadEventHeader(byte* headerPtr, bool useHeaderCompression, ref EventPipeEventHeader eventData)
        {
            if (FileFormatVersionNumber <= 3)
            {
                EventPipeEventHeader.ReadFromFormatV3(headerPtr, ref eventData);
            }
            else // if (FileFormatVersionNumber == 4)
            {
                EventPipeEventHeader.ReadFromFormatV4(headerPtr, useHeaderCompression, ref eventData);
                if(eventData.MetaDataId != 0 && StackCache.TryGetStack(eventData.StackId, out int stackBytesSize, out IntPtr stackBytes))
                {
                    eventData.StackBytesSize = stackBytesSize;
                    eventData.StackBytes = stackBytes;
                }
            }

            // Basic sanity checks.  Are the timestamps and sizes sane.
            Debug.Assert(sessionEndTimeQPC <= eventData.TimeStamp);
            Debug.Assert(sessionEndTimeQPC == 0 || eventData.TimeStamp - sessionEndTimeQPC < _QPCFreq * 24 * 3600);
            Debug.Assert(0 <= eventData.PayloadSize && eventData.PayloadSize <= eventData.TotalNonHeaderSize);
            Debug.Assert(0 <= eventData.TotalNonHeaderSize && eventData.TotalNonHeaderSize < 0x20000);  // TODO really should be 64K but BulkSurvivingObjectRanges needs fixing.
            Debug.Assert(FileFormatVersionNumber != 3 ||
                ((long)eventData.Payload % 4 == 0 && eventData.TotalNonHeaderSize % 4 == 0)); // ensure 4 byte alignment
            Debug.Assert(0 <= eventData.StackBytesSize && eventData.StackBytesSize <= 800);
        }


        private TraceEventNativeMethods.EVENT_RECORD* ReadEvent(EventPipeEventHeader eventData, PinnedStreamReader reader)
        {
            StreamLabel headerStart = reader.Current;
            StreamLabel eventDataEnd = headerStart.Add(eventData.HeaderSize + eventData.TotalNonHeaderSize);

            TraceEventNativeMethods.EVENT_RECORD* ret = null;
            if (eventData.IsMetadata())
            {
                int payloadSize = eventData.PayloadSize;
                // Note that this skip invalidates the eventData pointer, so it is important to pull any fields out we need first.
                reader.Skip(eventData.HeaderSize);

                StreamLabel metadataV1Start = reader.Current;
                StreamLabel metaDataEnd = reader.Current.Add(payloadSize);

                // Read in the header (The header does not include payload parameter information)
                var metaDataHeader = new EventPipeEventMetaDataHeader(reader, payloadSize,
                    GetMetaDataVersion(FileFormatVersionNumber), PointerSize, _processId);

                DynamicTraceEventData eventTemplate = CreateTemplate(metaDataHeader);

                // If the metadata contains no parameter metadata, don't attempt to read it.
                if (!metaDataHeader.ContainsParameterMetadata)
                {
                    CreateDefaultParameters(eventTemplate);
                }
                else
                {
                    ParseEventParameters(eventTemplate, metaDataHeader, reader, metaDataEnd, NetTraceFieldLayoutVersion.V1);
                }

                while (reader.Current < metaDataEnd)
                {
                    // If we've already parsed the V1 metadata and there's more left to decode,
                    // then we have some tags to read
                    int tagLength = reader.ReadInt32();
                    EventPipeMetadataTag tag = (EventPipeMetadataTag)reader.ReadByte();
                    StreamLabel tagEndLabel = reader.Current.Add(tagLength);

                    if (tag == EventPipeMetadataTag.ParameterPayloadV2)
                    {
                        ParseEventParameters(eventTemplate, metaDataHeader, reader, tagEndLabel, NetTraceFieldLayoutVersion.V2);
                    }
                    else if (tag == EventPipeMetadataTag.Opcode)
                    {
                        Debug.Assert(tagLength == 1);
                        metaDataHeader.Opcode = reader.ReadByte();
                        SetOpcode(eventTemplate, metaDataHeader.Opcode);
                    }

                    // Skip any remaining bytes or unknown tags
                    reader.Goto(tagEndLabel);
                }

                Debug.Assert(reader.Current == metaDataEnd);

                _eventMetadataDictionary.Add(metaDataHeader.MetaDataId, metaDataHeader);
                _metadataTemplates[eventTemplate] = eventTemplate;

                Debug.Assert(eventData.StackBytesSize == 0, "Meta-data events should always have a empty stack");
            }
            else
            {
                ret = ConvertEventHeaderToRecord(ref eventData);
            }

            reader.Goto(eventDataEnd);

            return ret;
        }

        private static EventPipeMetaDataVersion GetMetaDataVersion(int fileFormatVersion)
        {
            switch(fileFormatVersion)
            {
                case 1:
                    return EventPipeMetaDataVersion.LegacyV1;
                case 2:
                    return EventPipeMetaDataVersion.LegacyV2;
                default:
                    return EventPipeMetaDataVersion.NetTrace;
            }
        }

        private TraceEventNativeMethods.EVENT_RECORD* ConvertEventHeaderToRecord(ref EventPipeEventHeader eventData)
        {
            if (_eventMetadataDictionary.TryGetValue(eventData.MetaDataId, out var metaData))
            {
                return metaData.GetEventRecordForEventData(eventData);
            }
            else
            {
                Debug.Assert(false, "Warning can't find metaData for ID " + eventData.MetaDataId.ToString("x"));
                return null;
            }
        }

        internal override unsafe Guid GetRelatedActivityID(TraceEventNativeMethods.EVENT_RECORD* eventRecord)
        {
            if(FileFormatVersionNumber >= 4)
            {
                return _relatedActivityId;
            }
            else
            {
                // Recover the EventPipeEventHeader from the payload pointer and then fetch from the header.
                return EventPipeEventHeader.GetRelatedActivityID((byte*)eventRecord->UserData);
            }
        }

        public void ToStream(Serializer serializer) => throw new InvalidOperationException("We dont ever serialize one of these in managed code so we don't need to implement ToSTream");

        public void FromStream(Deserializer deserializer)
        {
            FileFormatVersionNumber = deserializer.VersionBeingRead;

#if SUPPORT_V1_V2
            if (deserializer.VersionBeingRead < 3)
            {
                ForwardReference reference = deserializer.ReadForwardReference();
                _endOfEventStream = deserializer.ResolveForwardReference(reference, preserveCurrent: true);
            }
#endif
            // The start time is stored as a SystemTime which is a bunch of shorts, convert to DateTime.
            short year = deserializer.ReadInt16();
            short month = deserializer.ReadInt16();
            short dayOfWeek = deserializer.ReadInt16();
            short day = deserializer.ReadInt16();
            short hour = deserializer.ReadInt16();
            short minute = deserializer.ReadInt16();
            short second = deserializer.ReadInt16();
            short milliseconds = deserializer.ReadInt16();
            _syncTimeUTC = new DateTime(year, month, day, hour, minute, second, milliseconds, DateTimeKind.Utc);
            deserializer.Read(out _syncTimeQPC);
            deserializer.Read(out _QPCFreq);

            sessionStartTimeQPC = _syncTimeQPC;

            if (3 <= deserializer.VersionBeingRead)
            {
                deserializer.Read(out pointerSize);
                deserializer.Read(out _processId);
                deserializer.Read(out numberOfProcessors);
                deserializer.Read(out _expectedCPUSamplingRate);
            }
#if SUPPORT_V1_V2
            else
            {
                _processId = 0; // V1 && V2 tests expect 0 for process Id
                pointerSize = 8; // V1 EventPipe only supports Linux which is x64 only.
                numberOfProcessors = 1;
            }
#endif
        }

        private void EventCache_OnEvent(ref EventPipeEventHeader header)
        {
            if (header.MetaDataId != 0 && StackCache.TryGetStack(header.StackId, out int stackBytesSize, out IntPtr stackBytes))
            {
                header.StackBytesSize = stackBytesSize;
                header.StackBytes = stackBytes;
            }
            _relatedActivityId = header.RelatedActivityID;
            DispatchEventRecord(ConvertEventHeaderToRecord(ref header));
        }

        private void EventCache_OnEventsDropped(int droppedEventCount)
        {
            long totalLostEvents = _eventsLost + droppedEventCount;
            _eventsLost = (int)Math.Min(totalLostEvents, int.MaxValue);
        }

        internal bool TryGetTemplateFromMetadata(TraceEvent unhandledEvent, out DynamicTraceEventData template)
        {
            return _metadataTemplates.TryGetValue(unhandledEvent, out template);
        }

        private static void CreateDefaultParameters(DynamicTraceEventData eventTemplate)
        {
            eventTemplate.payloadNames = new string[0];
            eventTemplate.payloadFetches = new DynamicTraceEventData.PayloadFetch[0];
        }

        /// <summary>
        /// Given the EventPipe metaData header and a stream pointing at the serialized meta-data for the parameters for the
        /// event, create a new  DynamicTraceEventData that knows how to parse that event.
        /// ReaderForParameters.Current is advanced past the parameter information.
        /// </summary>
        private void ParseEventParameters(DynamicTraceEventData template, EventPipeEventMetaDataHeader eventMetaDataHeader, PinnedStreamReader readerForParameters,
            StreamLabel metadataBlobEnd, NetTraceFieldLayoutVersion fieldLayoutVersion)
        {
            DynamicTraceEventData.PayloadFetchClassInfo classInfo = null;

            // Read the count of event payload fields.
            int fieldCount = readerForParameters.ReadInt32();
            Debug.Assert(0 <= fieldCount && fieldCount < 0x4000);

            if (fieldCount > 0)
            {
                try
                {
                    // Recursively parse the metadata, building up a list of payload names and payload field fetch objects.
                    classInfo = ParseFields(readerForParameters, fieldCount, metadataBlobEnd, fieldLayoutVersion);
                }
                catch (FormatException)
                {
                    // If we encounter unparsable metadata, ignore the payloads of this event type but don't fail to parse the entire
                    // trace. This gives us more flexibility in the future to introduce new descriptive information.
                    classInfo = null;
                }
            }

            if (classInfo == null)
            {
                classInfo = CheckForWellKnownEventFields(eventMetaDataHeader);
                if (classInfo == null)
                {
                    classInfo = new DynamicTraceEventData.PayloadFetchClassInfo()
                    {
                        FieldNames = new string[0],
                        FieldFetches = new DynamicTraceEventData.PayloadFetch[0]
                    };
                }
            }

            template.payloadNames = classInfo.FieldNames;
            template.payloadFetches = classInfo.FieldFetches;

            return;
        }

        private void SetOpcode(DynamicTraceEventData template, int opcode)
        {
            template.opcode = (TraceEventOpcode)opcode;
            template.opcodeName = template.opcode.ToString();
        }

        private DynamicTraceEventData CreateTemplate(EventPipeEventMetaDataHeader eventMetaDataHeader)
        {
            string opcodeName = ((TraceEventOpcode)eventMetaDataHeader.Opcode).ToString();
            int opcode = eventMetaDataHeader.Opcode;
            if (opcode == 0)
            {
                GetOpcodeFromEventName(eventMetaDataHeader.EventName, out opcode, out opcodeName);
            }
            string eventName = FilterOpcodeNameFromEventName(eventMetaDataHeader.EventName, opcode);
            DynamicTraceEventData template = new DynamicTraceEventData(null, eventMetaDataHeader.EventId, 0, eventName, Guid.Empty, opcode, null, eventMetaDataHeader.ProviderId, eventMetaDataHeader.ProviderName);
            SetOpcode(template, eventMetaDataHeader.Opcode);
            return template;
        }

        private string FilterOpcodeNameFromEventName(string eventName, int opcode)
        {
            // If the event has an opcode associated and the opcode name is also specified, we should
            // remove the opcode name from the event's name. Otherwise the events will show up with
            // duplicate opcode names (i.e. RequestStart/Start)
            if (opcode == (int)TraceEventOpcode.Start && eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase))
            {
                return eventName.Remove(eventName.Length - 5, 5);
            }
            else if (opcode == (int)TraceEventOpcode.Stop && eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase))
            {
                return eventName.Remove(eventName.Length - 4, 4);
            }
            return eventName;
        }

        // The NetPerf and NetTrace V1 file formats were incapable of representing some event parameter types that EventSource and ETW support.
        // This works around that issue without requiring a runtime or format update for well-known EventSources that used the indescribable types.
        private DynamicTraceEventData.PayloadFetchClassInfo CheckForWellKnownEventFields(EventPipeEventMetaDataHeader eventMetaDataHeader)
        {
            if (eventMetaDataHeader.ProviderName == "Microsoft-Diagnostics-DiagnosticSource")
            {
                string eventName = eventMetaDataHeader.EventName;

                if (eventName == "Event" ||
                   eventName == "Activity1Start" ||
                   eventName == "Activity1Stop" ||
                   eventName == "Activity2Start" ||
                   eventName == "Activity2Stop" ||
                   eventName == "RecursiveActivity1Start" ||
                   eventName == "RecursiveActivity1Stop")
                {
                    DynamicTraceEventData.PayloadFetch[] fieldFetches = new DynamicTraceEventData.PayloadFetch[3];
                    string[] fieldNames = new string[3];
                    fieldFetches[0].Type = typeof(string);
                    fieldFetches[0].Size = DynamicTraceEventData.NULL_TERMINATED;
                    fieldFetches[0].Offset = 0;
                    fieldNames[0] = "SourceName";

                    fieldFetches[1].Type = typeof(string);
                    fieldFetches[1].Size = DynamicTraceEventData.NULL_TERMINATED;
                    fieldFetches[1].Offset = ushort.MaxValue;
                    fieldNames[1] = "EventName";

                    DynamicTraceEventData.PayloadFetch[] keyValuePairFieldFetches = new DynamicTraceEventData.PayloadFetch[2];
                    string[] keyValuePairFieldNames = new string[2];
                    keyValuePairFieldFetches[0].Type = typeof(string);
                    keyValuePairFieldFetches[0].Size = DynamicTraceEventData.NULL_TERMINATED;
                    keyValuePairFieldFetches[0].Offset = 0;
                    keyValuePairFieldNames[0] = "Key";
                    keyValuePairFieldFetches[1].Type = typeof(string);
                    keyValuePairFieldFetches[1].Size = DynamicTraceEventData.NULL_TERMINATED;
                    keyValuePairFieldFetches[1].Offset = ushort.MaxValue;
                    keyValuePairFieldNames[1] = "Value";
                    DynamicTraceEventData.PayloadFetchClassInfo keyValuePairClassInfo = new DynamicTraceEventData.PayloadFetchClassInfo()
                    {
                        FieldFetches = keyValuePairFieldFetches,
                        FieldNames = keyValuePairFieldNames
                    };
                    DynamicTraceEventData.PayloadFetch argumentElementFetch = DynamicTraceEventData.PayloadFetch.StructPayloadFetch(0, keyValuePairClassInfo);
                    ushort fetchSize = DynamicTraceEventData.COUNTED_SIZE + DynamicTraceEventData.ELEM_COUNT;
                    fieldFetches[2] = DynamicTraceEventData.PayloadFetch.ArrayPayloadFetch(ushort.MaxValue, argumentElementFetch, fetchSize);
                    fieldNames[2] = "Arguments";

                    return new DynamicTraceEventData.PayloadFetchClassInfo()
                    {
                        FieldFetches = fieldFetches,
                        FieldNames = fieldNames
                    };
                }
            }

            return null;
        }

        private DynamicTraceEventData.PayloadFetchClassInfo ParseFields(PinnedStreamReader reader, int numFields, StreamLabel metadataBlobEnd, 
            NetTraceFieldLayoutVersion fieldLayoutVersion)
        {
            string[] fieldNames = new string[numFields];
            DynamicTraceEventData.PayloadFetch[] fieldFetches = new DynamicTraceEventData.PayloadFetch[numFields];

            ushort offset = 0;
            for (int fieldIndex = 0; fieldIndex < numFields; fieldIndex++)
            {
                StreamLabel fieldEnd = metadataBlobEnd;
                string fieldName = "<unknown_field>";
                if (fieldLayoutVersion >= NetTraceFieldLayoutVersion.V2)
                {
                    StreamLabel fieldStart = reader.Current;
                    int fieldLength = reader.ReadInt32();
                    fieldEnd = fieldStart.Add(fieldLength);
                    Debug.Assert(fieldEnd <= metadataBlobEnd);

                    fieldName = reader.ReadNullTerminatedUnicodeString();
                }

                DynamicTraceEventData.PayloadFetch payloadFetch = ParseType(reader, offset, fieldEnd, fieldName, fieldLayoutVersion);

                if (fieldLayoutVersion <= NetTraceFieldLayoutVersion.V1)
                {
                    // Read the string name of the event payload field.
                    // The older format put the name after the type signature rather
                    // than before it. This is a bit worse for diagnostics because
                    // we won't have the name available to associate with any failure
                    // reading the type signature above.
                    fieldName = reader.ReadNullTerminatedUnicodeString();
                }

                fieldNames[fieldIndex] = fieldName;

                // Update the offset into the event for the next payload fetch.
                if (payloadFetch.Size >= DynamicTraceEventData.SPECIAL_SIZES || offset == ushort.MaxValue)
                {
                    offset = ushort.MaxValue;           // Indicate that the offset must be computed at run time.
                }
                else
                {
                    offset += payloadFetch.Size;
                }

                // Save the current payload fetch.
                fieldFetches[fieldIndex] = payloadFetch;

                if (fieldLayoutVersion >= NetTraceFieldLayoutVersion.V2)
                {
                    // skip over any data that a later version of the format may append
                    Debug.Assert(reader.Current <= fieldEnd);
                    reader.Goto(fieldEnd);
                }
            }

            return new DynamicTraceEventData.PayloadFetchClassInfo()
            {
                FieldNames = fieldNames,
                FieldFetches = fieldFetches
            };
        }

        private DynamicTraceEventData.PayloadFetch ParseType(
            PinnedStreamReader reader,
            ushort offset,
            StreamLabel fieldEnd,
            string fieldName,
            NetTraceFieldLayoutVersion fieldLayoutVersion)
        {
            Debug.Assert(reader.Current < fieldEnd);
            DynamicTraceEventData.PayloadFetch payloadFetch = new DynamicTraceEventData.PayloadFetch();

            // Read the TypeCode for the current field.
            TypeCode typeCode = (TypeCode)reader.ReadInt32();

            // Fill out the payload fetch object based on the TypeCode.
            switch (typeCode)
            {
                case TypeCode.Boolean:
                    {
                        payloadFetch.Type = typeof(bool);
                        payloadFetch.Size = 4; // We follow windows conventions and use 4 bytes for bool.
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Char:
                    {
                        payloadFetch.Type = typeof(char);
                        payloadFetch.Size = sizeof(char);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.SByte:
                    {
                        payloadFetch.Type = typeof(SByte);
                        payloadFetch.Size = sizeof(SByte);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Byte:
                    {
                        payloadFetch.Type = typeof(byte);
                        payloadFetch.Size = sizeof(byte);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Int16:
                    {
                        payloadFetch.Type = typeof(Int16);
                        payloadFetch.Size = sizeof(Int16);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.UInt16:
                    {
                        payloadFetch.Type = typeof(UInt16);
                        payloadFetch.Size = sizeof(UInt16);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Int32:
                    {
                        payloadFetch.Type = typeof(Int32);
                        payloadFetch.Size = sizeof(Int32);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.UInt32:
                    {
                        payloadFetch.Type = typeof(UInt32);
                        payloadFetch.Size = sizeof(UInt32);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Int64:
                    {
                        payloadFetch.Type = typeof(Int64);
                        payloadFetch.Size = sizeof(Int64);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.UInt64:
                    {
                        payloadFetch.Type = typeof(UInt64);
                        payloadFetch.Size = sizeof(UInt64);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Single:
                    {
                        payloadFetch.Type = typeof(Single);
                        payloadFetch.Size = sizeof(Single);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Double:
                    {
                        payloadFetch.Type = typeof(Double);
                        payloadFetch.Size = sizeof(Double);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Decimal:
                    {
                        payloadFetch.Type = typeof(Decimal);
                        payloadFetch.Size = sizeof(Decimal);
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.DateTime:
                    {
                        payloadFetch.Type = typeof(DateTime);
                        payloadFetch.Size = 8;
                        payloadFetch.Offset = offset;
                        break;
                    }
                case EventPipeEventSource.GuidTypeCode:
                    {
                        payloadFetch.Type = typeof(Guid);
                        payloadFetch.Size = 16;
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.String:
                    {
                        payloadFetch.Type = typeof(String);
                        payloadFetch.Size = DynamicTraceEventData.NULL_TERMINATED;
                        payloadFetch.Offset = offset;
                        break;
                    }
                case TypeCode.Object:
                    {
                        // TypeCode.Object represents an embedded struct.

                        // Read the number of fields in the struct.  Each of these fields could be an embedded struct,
                        // but these embedded structs are still counted as single fields.  They will be expanded when they are handled.
                        int structFieldCount = reader.ReadInt32();
                        DynamicTraceEventData.PayloadFetchClassInfo embeddedStructClassInfo = ParseFields(reader, structFieldCount, fieldEnd, fieldLayoutVersion);
                        if (embeddedStructClassInfo == null)
                        {
                            throw new FormatException($"Field {fieldName}: Unable to parse metadata for embedded struct");
                        }
                        payloadFetch = DynamicTraceEventData.PayloadFetch.StructPayloadFetch(offset, embeddedStructClassInfo);
                        break;
                    }

                case EventPipeEventSource.ArrayTypeCode:
                    {
                        if (fieldLayoutVersion == NetTraceFieldLayoutVersion.V1)
                        {
                            throw new FormatException($"EventPipeEventSource.ArrayTypeCode is not a valid type code in V1 field metadata.");
                        }

                        DynamicTraceEventData.PayloadFetch elementType = ParseType(reader, 0, fieldEnd, fieldName, fieldLayoutVersion);
                        // This fetchSize marks the array as being prefixed with an unsigned 16 bit count of elements
                        ushort fetchSize = DynamicTraceEventData.COUNTED_SIZE + DynamicTraceEventData.ELEM_COUNT;
                        payloadFetch = DynamicTraceEventData.PayloadFetch.ArrayPayloadFetch(offset, elementType, fetchSize);
                        break;
                    }
                default:
                    {
                        throw new FormatException($"Field {fieldName}: Typecode {typeCode} is not supported.");
                    }
            }

            return payloadFetch;
        }

        private static void GetOpcodeFromEventName(string eventName, out int opcode, out string opcodeName)
        {
            opcode = 0;
            opcodeName = null;
            // If this EventName suggests that it has an Opcode, then we must remove the opcode name from its name
            // Otherwise the events will show up with duplicate opcode names (i.e. RequestStart/Start)

            if (eventName != null)
            {
                if (eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase))
                {
                    opcode = (int)TraceEventOpcode.Start;
                    opcodeName = nameof(TraceEventOpcode.Start);
                }
                else if (eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase))
                {
                    opcode = (int)TraceEventOpcode.Stop;
                    opcodeName = nameof(TraceEventOpcode.Stop);
                }
            }
        }

        // Guid is not part of TypeCode (yet), we decided to use 17 to represent it, as it's the "free slot"
        // see https://github.com/dotnet/coreclr/issues/16105#issuecomment-361749750 for more
        internal const TypeCode GuidTypeCode = (TypeCode)17;
        // Array isn't part of TypeCode either
        internal const TypeCode ArrayTypeCode = (TypeCode)19;

#if SUPPORT_V1_V2
        private StreamLabel _endOfEventStream;
#endif
        private Dictionary<int, EventPipeEventMetaDataHeader> _eventMetadataDictionary = new Dictionary<int, EventPipeEventMetaDataHeader>();
        private Deserializer _deserializer;
        private Dictionary<TraceEvent, DynamicTraceEventData> _metadataTemplates =
            new Dictionary<TraceEvent, DynamicTraceEventData>(new ExternalTraceEventParserState.TraceEventComparer());
        private EventPipeEventHeader _compressedHeader;
        private int _eventsLost;
        private Guid _relatedActivityId;
        internal int _processId;
        internal int _expectedCPUSamplingRate;
        #endregion
    }

    #region private classes

    /// <summary>
    /// The Nettrace format is divided up into various blocks - this is a base class that handles the common
    /// aspects for all of them.
    /// </summary>
    internal abstract class EventPipeBlock : IFastSerializable, IFastSerializableVersion
    {
        public EventPipeBlock(EventPipeEventSource source) => _source = source;

        // _startEventData and _endEventData have already been initialized before this is invoked
        // to help identify the bounds. The reader is positioned at _startEventData
        protected abstract void ReadBlockContents(PinnedStreamReader reader);

        public unsafe void FromStream(Deserializer deserializer)
        {
            // blockSizeInBytes does not include padding bytes to ensure alignment.
            var blockSizeInBytes = deserializer.ReadInt();

            // after the block size comes eventual padding, we just need to skip it by jumping to the nearest aligned address
            if ((long)deserializer.Current % 4 != 0)
            {
                var nearestAlignedAddress = deserializer.Current.Add((int)(4 - ((long)deserializer.Current % 4)));
                deserializer.Goto(nearestAlignedAddress);
            }

            _startEventData = deserializer.Current;
            _endEventData = _startEventData.Add(blockSizeInBytes);

            PinnedStreamReader deserializerReader = (PinnedStreamReader)deserializer.Reader;
            ReadBlockContents(deserializerReader);
            deserializerReader.Goto(_endEventData); // go to the end of block, in case some padding was not skipped yet
        }

        public void ToStream(Serializer serializer) => throw new InvalidOperationException();

        protected StreamLabel _startEventData;
        protected StreamLabel _endEventData;
        protected EventPipeEventSource _source;

        public int Version => 2;

        public int MinimumVersionCanRead => Version;

        public int MinimumReaderVersion => 0;
    }

    internal enum EventBlockFlags : short
    {
        Uncompressed = 0,
        HeaderCompression = 1
    }

    /// <summary>
    /// An EVentPipeEventBlock represents a block of events.   It basicaly only has
    /// one field, which is the size in bytes of the block.  But when its FromStream
    /// is called, it will perform the callbacks for the events (thus deserializing
    /// it performs dispatch).
    /// </summary>
    internal class EventPipeEventBlock : EventPipeBlock
    {
        public EventPipeEventBlock(EventPipeEventSource source) : base(source) { }

        protected unsafe override void ReadBlockContents(PinnedStreamReader reader)
        {
            if(_source.FileFormatVersionNumber >= 4)
            {
                _source.ResetCompressedHeader();
                byte[] eventBlockBytes = new byte[_endEventData.Sub(_startEventData)];
                reader.Read(eventBlockBytes, 0, eventBlockBytes.Length);
                _source.EventCache.ProcessEventBlock(eventBlockBytes);
            }
            else
            {
                //NetPerf file had the events fully sorted so we can dispatch directly
                while (reader.Current < _endEventData)
                {
                    _source.ReadAndDispatchEvent(reader, false);
                }
            }
        }
    }

    /// <summary>
    /// A block of metadata carrying events. These 'events' aren't dispatched by EventPipeEventSource - they carry
    /// the metadata that allows the payloads of non-metadata events to be decoded.
    /// </summary>
    internal class EventPipeMetadataBlock : EventPipeBlock
    {
        public EventPipeMetadataBlock(EventPipeEventSource source) : base(source) { }

        protected unsafe override void ReadBlockContents(PinnedStreamReader reader)
        {
            _source.ResetCompressedHeader();
            short headerSize = reader.ReadInt16();
            Debug.Assert(headerSize >= 20);
            short flags = reader.ReadInt16();
            long minTimeStamp = reader.ReadInt64();
            long maxTimeStamp = reader.ReadInt64();

            reader.Goto(_startEventData.Add(headerSize));
            while (reader.Current < _endEventData)
            {
                _source.ReadAndDispatchEvent(reader, (flags & (short)EventBlockFlags.HeaderCompression) != 0);
            }
        }
    }

    /// <summary>
    /// An EventPipeSequencePointBlock represents a stream divider that contains
    /// updates for all thread event sequence numbers, indicates that all queued
    /// events can be sorted and dispatched, and that all cached events/stacks can
    /// be flushed.
    /// </summary>
    internal class EventPipeSequencePointBlock : EventPipeBlock
    {
        public EventPipeSequencePointBlock(EventPipeEventSource source) : base(source) { }

        protected unsafe override void ReadBlockContents(PinnedStreamReader reader)
        {
            byte[] blockBytes = new byte[_endEventData.Sub(_startEventData)];
            reader.Read(blockBytes, 0, blockBytes.Length);
            _source.EventCache.ProcessSequencePointBlock(blockBytes);
            _source.StackCache.Flush();
        }
    }

    /// <summary>
    /// An EventPipeStackBlock represents a block of interned stacks. Events refer
    /// to stacks by an id.
    /// </summary>
    internal class EventPipeStackBlock : EventPipeBlock
    {
        public EventPipeStackBlock(EventPipeEventSource source) : base(source) { }

        protected unsafe override void ReadBlockContents(PinnedStreamReader reader)
        {
            byte[] stackBlockBytes = new byte[_endEventData.Sub(_startEventData)];
            reader.Read(stackBlockBytes, 0, stackBlockBytes.Length);
            _source.StackCache.ProcessStackBlock(stackBlockBytes);
        }
    }

    internal enum EventPipeMetaDataVersion
    {
        LegacyV1 = 1, // Used by NetPerf version 1
        LegacyV2 = 2, // Used by NetPerf version 2
        NetTrace = 3, // Used by NetPerf (version 3) and NetTrace (version 4+)
    }

    internal enum NetTraceFieldLayoutVersion
    {
        V1 = 1, // Used by V1 parameter blobs
        V2 = 2 // Use by V2 parameter blobs
    }

    internal enum EventPipeMetadataTag
    {
        Opcode = 1,
        ParameterPayloadV2 = 2
    }

    /// <summary>
    /// Private utility class.
    ///
    /// An EventPipeEventMetaDataHeader holds the information that can be shared among all
    /// instances of an EventPipe event from a particular provider.   Thus it contains
    /// things like the event name, provider, It however does NOT contain the data
    /// about the event parameters (the names of the fields and their types), That is
    /// why this is a meta-data header and not all the meta-data.
    ///
    /// This class has two main functions
    ///    1. The constructor takes a PinnedStreamReader and decodes the serialized metadata
    ///       so you can access the data conveniently (but it does not decode the parameter info)
    ///    2. It remembers a EVENT_RECORD structure (from ETW) that contains this data)
    ///       and has a function GetEventRecordForEventData which converts from a
    ///       EventPipeEventHeader (the raw serialized data) to a EVENT_RECORD (which
    ///       is what TraceEvent needs to look up the event an pass it up the stack.
    /// </summary>
    internal unsafe class EventPipeEventMetaDataHeader
    {
        /// <summary>
        /// Creates a new MetaData instance from the serialized data at the current position of 'reader'
        /// of length 'length'.   This typically points at the PAYLOAD AREA of a meta-data events)
        /// 'fileFormatVersionNumber' is the version number of the file as a whole
        /// (since that affects the parsing of this data) and 'processID' is the process ID for the
        /// whole stream (since it needs to be put into the EVENT_RECORD.
        ///
        /// When this constructor returns the reader has read up to the serialized information about
        /// the parameters.  We do this because this code does not know the best representation for
        /// this parameter information and so it just lets other code handle it.
        /// </summary>
        public EventPipeEventMetaDataHeader(PinnedStreamReader reader, int length, EventPipeMetaDataVersion encodingVersion,
                                            int pointerSize, int processId, int metadataId = 0, string providerName = null)
        {
            // Get the event record and fill in fields that we can without deserializing anything.
            _eventRecord = (TraceEventNativeMethods.EVENT_RECORD*)Marshal.AllocHGlobal(sizeof(TraceEventNativeMethods.EVENT_RECORD));
            ClearMemory(_eventRecord, sizeof(TraceEventNativeMethods.EVENT_RECORD));

            if (pointerSize == 4)
            {
                _eventRecord->EventHeader.Flags = TraceEventNativeMethods.EVENT_HEADER_FLAG_32_BIT_HEADER;
            }
            else
            {
                _eventRecord->EventHeader.Flags = TraceEventNativeMethods.EVENT_HEADER_FLAG_64_BIT_HEADER;
            }

            _eventRecord->EventHeader.ProcessId = processId;
            EncodingVersion = encodingVersion;

            // Calculate the position of the end of the metadata blob.
            StreamLabel metadataEndLabel = reader.Current.Add(length);

            // Read the metaData
            if (encodingVersion >= EventPipeMetaDataVersion.NetTrace)
            {
                ReadNetTraceMetadata(reader);
            }
#if SUPPORT_V1_V2
            else
            {
                ReadObsoleteEventMetaData(reader, encodingVersion);
            }
#endif

            // Check for parameter metadata so that it can be consumed by the parser.
            if (reader.Current < metadataEndLabel)
            {
                ContainsParameterMetadata = true;
            }
        }

        ~EventPipeEventMetaDataHeader()
        {
            if (_eventRecord != null)
            {
                if (_eventRecord->ExtendedData != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_eventRecord->ExtendedData);
                }

                Marshal.FreeHGlobal((IntPtr)_eventRecord);
                _eventRecord = null;
            }
        }

        /// <summary>
        /// Given a EventPipeEventHeader takes a EventPipeEventHeader that is specific to an event, copies it
        /// on top of the static information in its EVENT_RECORD which is specialized meta-data
        /// and returns a pointer to it.  Thus this makes the EventPipe look like an ETW provider from
        /// the point of view of the upper level TraceEvent logic.
        /// </summary>
        internal TraceEventNativeMethods.EVENT_RECORD* GetEventRecordForEventData(in EventPipeEventHeader eventData)
        {
            // We have already initialize all the fields of _eventRecord that do no vary from event to event.
            // Now we only have to copy over the fields that are specific to particular event.
            //
            // Note: ThreadId isn't 32 bit on all of our platforms but ETW EVENT_RECORD* only has room for a 32 bit
            // ID. We'll need to refactor up the stack if we want to expose a bigger ID.
            _eventRecord->EventHeader.ThreadId = unchecked((int)eventData.ThreadId);
            if (eventData.ThreadId == eventData.CaptureThreadId && eventData.CaptureProcNumber != -1)
            {
                // Its not clear how the caller is supposed to distinguish between events that we know were on
                // processor 0 vs. lacking information about what processor number the thread is on and
                // reporting 0. We could certainly change the API to make this more apparent, but for now I
                // am only focused on ensuring the data is in the file format and we could improve access in the
                // future.
                _eventRecord->BufferContext.ProcessorNumber = (byte)eventData.CaptureProcNumber;
            }
            _eventRecord->EventHeader.TimeStamp = eventData.TimeStamp;
            _eventRecord->EventHeader.ActivityId = eventData.ActivityID;
            // EVENT_RECORD does not field for ReleatedActivityID (because it is rarely used).  See GetRelatedActivityID;
            _eventRecord->UserDataLength = (ushort)eventData.PayloadSize;

            // TODO the extra || operator is a hack because the runtime actually tries to emit events that
            // exceed this for the GC/BulkSurvivingObjectRanges (event id == 21).  We suppress that assert
            // for now but this is a real bug in the runtime's event logging.  ETW can't handle payloads > 64K.
            Debug.Assert(_eventRecord->UserDataLength == eventData.PayloadSize ||
                _eventRecord->EventHeader.ProviderId == ClrTraceEventParser.ProviderGuid && _eventRecord->EventHeader.Id == 21);
            _eventRecord->UserData = eventData.Payload;

            int stackBytesSize = eventData.StackBytesSize;

            // TODO remove once .NET Core has been fixed to not emit stacks on CLR method events which are just for bookkeeping.
            if (ProviderId == ClrRundownTraceEventParser.ProviderGuid ||
               (ProviderId == ClrTraceEventParser.ProviderGuid && (140 <= EventId && EventId <= 144 || EventId == 190)))     // These are various CLR method Events.
            {
                stackBytesSize = 0;
            }

            if (0 < stackBytesSize)
            {
                // Lazy allocation (destructor frees it).
                if (_eventRecord->ExtendedData == null)
                {
                    _eventRecord->ExtendedData = (TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM*)Marshal.AllocHGlobal(sizeof(TraceEventNativeMethods.EVENT_HEADER_EXTENDED_DATA_ITEM));
                }

                if ((_eventRecord->EventHeader.Flags & TraceEventNativeMethods.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0)
                {
                    _eventRecord->ExtendedData->ExtType = TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_STACK_TRACE32;
                }
                else
                {
                    _eventRecord->ExtendedData->ExtType = TraceEventNativeMethods.EVENT_HEADER_EXT_TYPE_STACK_TRACE64;
                }

                // DataPtr should point at a EVENT_EXTENDED_ITEM_STACK_TRACE*.  These have a ulong MatchID field which is NOT USED before the stack data.
                // Since that field is not used, I can backup the pointer by 8 bytes and synthesize a EVENT_EXTENDED_ITEM_STACK_TRACE from the raw buffer
                // of stack data without having to copy.
                _eventRecord->ExtendedData->DataSize = (ushort)(stackBytesSize + 8);
                _eventRecord->ExtendedData->DataPtr = (ulong)(eventData.StackBytes - 8);

                _eventRecord->ExtendedDataCount = 1;        // Mark that we have the stack data.
            }
            else
            {
                _eventRecord->ExtendedDataCount = 0;
            }

            return _eventRecord;
        }

        /// <summary>
        /// This is a number that is unique to this meta-data blob.  It is expected to be a small integer
        /// that starts at 1 (since 0 is reserved) and increases from there (thus an array can be used).
        /// It is what is matched up with EventPipeEventHeader.MetaDataId
        /// </summary>
        public int MetaDataId { get; internal set; }
        public bool ContainsParameterMetadata { get; private set; }
        public string ProviderName { get; internal set; }
        public string EventName { get; private set; }
        public Guid ProviderId { get { return _eventRecord->EventHeader.ProviderId; } }
        public int EventId { get { return _eventRecord->EventHeader.Id; } }
        public int EventVersion { get { return _eventRecord->EventHeader.Version; } }
        public ulong Keywords { get { return _eventRecord->EventHeader.Keyword; } }
        public int Level { get { return _eventRecord->EventHeader.Level; } }
        public EventPipeMetaDataVersion EncodingVersion { get; internal set; }
        public byte Opcode { get { return _eventRecord->EventHeader.Opcode; } internal set { _eventRecord->EventHeader.Opcode = (byte)value; } }

        /// <summary>
        /// Reads the meta data for information specific to one event.
        /// </summary>
        private void ReadNetTraceMetadata(PinnedStreamReader reader)
        {
            MetaDataId = reader.ReadInt32();
            ProviderName = reader.ReadNullTerminatedUnicodeString();
            _eventRecord->EventHeader.ProviderId = GetProviderGuidFromProviderName(ProviderName);
            ReadMetadataCommon(reader);
        }

        private void ReadMetadataCommon(PinnedStreamReader reader)
        {
            int eventId = (ushort)reader.ReadInt32();
            _eventRecord->EventHeader.Id = (ushort)eventId;
            Debug.Assert(_eventRecord->EventHeader.Id == eventId);  // No truncation

            EventName = reader.ReadNullTerminatedUnicodeString();

            // Deduce the opcode from the name.
            if (EventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase))
            {
                _eventRecord->EventHeader.Opcode = (byte)TraceEventOpcode.Start;
            }
            else if (EventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase))
            {
                _eventRecord->EventHeader.Opcode = (byte)TraceEventOpcode.Stop;
            }
            if(EventName == "")
            {
                EventName = null; //TraceEvent expects empty name to be canonicalized as null rather than ""
            }

            _eventRecord->EventHeader.Keyword = (ulong)reader.ReadInt64();

            int version = reader.ReadInt32();
            _eventRecord->EventHeader.Version = (byte)version;
            Debug.Assert(_eventRecord->EventHeader.Version == version);  // No truncation

            _eventRecord->EventHeader.Level = (byte)reader.ReadInt32();
            Debug.Assert(_eventRecord->EventHeader.Level <= 5);
        }

#if SUPPORT_V1_V2
        private void ReadObsoleteEventMetaData(PinnedStreamReader reader, EventPipeMetaDataVersion metaDataVersion)
        {
            Debug.Assert((int)metaDataVersion <= (int)EventPipeMetaDataVersion.LegacyV2);

            // Old versions use the stream offset as the MetaData ID, but the reader has advanced to the payload so undo it.
            MetaDataId = ((int)reader.Current) - EventPipeEventHeader.GetHeaderSize((int)metaDataVersion);

            if (metaDataVersion == EventPipeMetaDataVersion.LegacyV1)
            {
                _eventRecord->EventHeader.ProviderId = reader.ReadGuid();
            }
            else
            {
                ProviderName = reader.ReadNullTerminatedUnicodeString();
                _eventRecord->EventHeader.ProviderId = GetProviderGuidFromProviderName(ProviderName);
            }

            var eventId = (ushort)reader.ReadInt32();
            _eventRecord->EventHeader.Id = eventId;
            Debug.Assert(_eventRecord->EventHeader.Id == eventId);  // No truncation

            var version = reader.ReadInt32();
            _eventRecord->EventHeader.Version = (byte)version;
            Debug.Assert(_eventRecord->EventHeader.Version == version);  // No truncation

            int metadataLength = reader.ReadInt32();
            if (0 < metadataLength)
            {
                ReadMetadataCommon(reader);
            }
        }
#endif

        // this is a memset implementation.  Note that we often use the trick of assigning a pointer to a struct to *ptr = default(Type);
        // Span.Clear also now does this.
        private static void ClearMemory(void* buffer, int length)
        {
            byte* ptr = (byte*)buffer;
            while (length > 0)
            {
                *ptr++ = 0;
                --length;
            }
        }

        public static Guid GetProviderGuidFromProviderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Guid.Empty;
            }

            // Legacy GUID lookups (events which existed before the current Guid generation conventions)
            if (name == TplEtwProviderTraceEventParser.ProviderName)
            {
                return TplEtwProviderTraceEventParser.ProviderGuid;
            }
            else if (name == ClrTraceEventParser.ProviderName)
            {
                return ClrTraceEventParser.ProviderGuid;
            }
            else if (name == ClrPrivateTraceEventParser.ProviderName)
            {
                return ClrPrivateTraceEventParser.ProviderGuid;
            }
            else if (name == ClrRundownTraceEventParser.ProviderName)
            {
                return ClrRundownTraceEventParser.ProviderGuid;
            }
            else if (name == ClrStressTraceEventParser.ProviderName)
            {
                return ClrStressTraceEventParser.ProviderGuid;
            }
            else if (name == FrameworkEventSourceTraceEventParser.ProviderName)
            {
                return FrameworkEventSourceTraceEventParser.ProviderGuid;
            }
#if SUPPORT_V1_V2
            else if (name == SampleProfilerTraceEventParser.ProviderName)
            {
                return SampleProfilerTraceEventParser.ProviderGuid;
            }
#endif
            // Hash the name according to current event source naming conventions
            else
            {
                return TraceEventProviders.GetEventSourceGuidFromName(name);
            }
        }

        private TraceEventNativeMethods.EVENT_RECORD* _eventRecord;
    }

    /// <summary>
    /// Private utility class.
    ///
    /// At the start of every event from an EventPipe is a header that contains
    /// common fields like its size, threadID timestamp etc.  EventPipeEventHeader
    /// is the layout of this.  Events have two variable sized parts: the user
    /// defined fields, and the stack.   EventPipEventHeader knows how to
    /// decode these pieces (but provides no semantics for it.
    ///
    /// It is not a public type, but used in low level parsing of EventPipeEventSource.
    /// </summary>

    internal unsafe struct EventPipeEventHeader
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct LayoutV3
        {
            public int EventSize;          // Size bytes of this header and the payload and stacks if any.  does NOT encode the size of the EventSize field itself.
            public int MetaDataId;          // a number identifying the description of this event.
            public int ThreadId;
            public long TimeStamp;
            public Guid ActivityID;
            public Guid RelatedActivityID;
            public int PayloadSize;         // size in bytes of the user defined payload data.
            public fixed byte Payload[4];   // Actually of variable size.  4 is used to avoid potential alignment issues.   This 4 also appears in HeaderSize below.
        }

        public static void ReadFromFormatV3(byte* headerPtr, ref EventPipeEventHeader header)
        {
            LayoutV3* pLayout = (LayoutV3*)headerPtr;
            header.EventSize = pLayout->EventSize;
            header.MetaDataId = pLayout->MetaDataId;
            header.ThreadId = pLayout->ThreadId;
            header.CaptureThreadId = -1;
            header.CaptureProcNumber = -1;
            header.TimeStamp = pLayout->TimeStamp;
            header.ActivityID = pLayout->ActivityID;
            header.RelatedActivityID = pLayout->RelatedActivityID;
            header.PayloadSize = pLayout->PayloadSize;
            header.Payload = (IntPtr)pLayout->Payload;
            header.StackBytesSize = *((int*)(&pLayout->Payload[pLayout->PayloadSize]));
            header.StackBytes = (IntPtr)(&pLayout->Payload[pLayout->PayloadSize + 4]);
            header.HeaderSize = (sizeof(LayoutV3) - 4);
            int totalSize = header.EventSize + 4;
            header.TotalNonHeaderSize = totalSize - header.HeaderSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct LayoutV4
        {
            public int EventSize;          // Size bytes of this header and the payload and stacks if any.  does NOT encode the size of the EventSize field itself.
            public int MetaDataId;          // a number identifying the description of this event.
            public int SequenceNumber;
            public long ThreadId;
            public long CaptureThreadId;
            public int CaptureProcNumber;
            public int StackId;
            public long TimeStamp;
            public Guid ActivityID;
            public Guid RelatedActivityID;
            public int PayloadSize;         // size in bytes of the user defined payload data.
            public fixed byte Payload[4];   // Actually of variable size.  4 is used to avoid potential alignment issues.   This 4 also appears in HeaderSize below.
        }

        enum CompressedHeaderFlags
        {
            MetadataId = 1 << 0,
            CaptureThreadAndSequence = 1 << 1,
            ThreadId = 1 << 2,
            StackId = 1 << 3,
            ActivityId = 1 << 4,
            RelatedActivityId = 1 << 5,
            Sorted = 1 << 6,
            DataLength = 1 << 7
        }

        static uint ReadVarUInt32(ref byte* pCursor)
        {
            uint val = 0;
            int shift = 0;
            byte b;
            do
            {
                if (shift == 5 * 7)
                {
                    Debug.Assert(false, "VarUInt32 is too long");
                    return val;
                }
                b = *pCursor;
                pCursor++;
                val |= (uint)(b & 0x7f) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return val;
        }

        static ulong ReadVarUInt64(ref byte* pCursor)
        {
            ulong val = 0;
            int shift = 0;
            byte b;
            do
            {
                if (shift == 10 * 7)
                {
                    Debug.Assert(false, "VarUInt64 is too long");
                    return val;
                }
                b = *pCursor;
                pCursor++;
                val |= (ulong)(b & 0x7f) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return val;
        }

        public static void ReadFromFormatV4(byte* headerPtr, bool useHeaderCompresion, ref EventPipeEventHeader header)
        {
            if (!useHeaderCompresion)
            {
                LayoutV4* pLayout = (LayoutV4*)headerPtr;
                header.EventSize = pLayout->EventSize;
                header.MetaDataId = pLayout->MetaDataId & 0x7FFF_FFFF;
                header.IsSorted = ((uint)pLayout->MetaDataId & 0x8000_0000) == 0;
                header.SequenceNumber = pLayout->SequenceNumber;
                header.ThreadId = pLayout->ThreadId;
                header.CaptureThreadId = pLayout->CaptureThreadId;
                header.CaptureProcNumber = pLayout->CaptureProcNumber;
                header.StackId = pLayout->StackId;
                header.TimeStamp = pLayout->TimeStamp;
                header.ActivityID = pLayout->ActivityID;
                header.RelatedActivityID = pLayout->RelatedActivityID;
                header.PayloadSize = pLayout->PayloadSize;
                header.Payload = (IntPtr)pLayout->Payload;
                header.HeaderSize = (sizeof(LayoutV4) - 4);
                int totalSize = header.EventSize + 4;
                header.TotalNonHeaderSize = totalSize - header.HeaderSize;
            }
            else
            {
                byte* headerStart = headerPtr;
                byte flags = *headerPtr;
                headerPtr++;
                if((flags & (byte)CompressedHeaderFlags.MetadataId) != 0)
                {
                    header.MetaDataId = (int)ReadVarUInt32(ref headerPtr);
                }
                if ((flags & (byte)CompressedHeaderFlags.CaptureThreadAndSequence) != 0)
                {
                    header.SequenceNumber += (int)ReadVarUInt32(ref headerPtr) + 1;
                    header.CaptureThreadId = (long)ReadVarUInt64(ref headerPtr);
                    header.CaptureProcNumber = (int)ReadVarUInt32(ref headerPtr);
                }
                else
                {
                    if(header.MetaDataId != 0)
                    {
                        header.SequenceNumber++;
                    }
                }
                if ((flags & (byte)CompressedHeaderFlags.ThreadId) != 0)
                {
                    header.ThreadId = (int)ReadVarUInt64(ref headerPtr);
                }
                if ((flags & (byte)CompressedHeaderFlags.StackId) != 0)
                {
                    header.StackId = (int)ReadVarUInt32(ref headerPtr);
                }
                ulong timestampDelta = ReadVarUInt64(ref headerPtr);
                header.TimeStamp += (long)timestampDelta;
                if ((flags & (byte)CompressedHeaderFlags.ActivityId) != 0)
                {
                    header.ActivityID = *(Guid*)headerPtr;
                    headerPtr += sizeof(Guid);
                }
                if ((flags & (byte)CompressedHeaderFlags.RelatedActivityId) != 0)
                {
                    header.RelatedActivityID = *(Guid*)headerPtr;
                    headerPtr += sizeof(Guid);
                }
                header.IsSorted = (flags & (byte)CompressedHeaderFlags.Sorted) != 0;
                if ((flags & (byte)CompressedHeaderFlags.DataLength) != 0)
                {
                    header.PayloadSize = (int)ReadVarUInt32(ref headerPtr);
                }
                header.Payload = (IntPtr)headerPtr;

                header.HeaderSize = (int)(headerPtr - headerStart);
                header.TotalNonHeaderSize = header.PayloadSize;
            }
        }

        private int EventSize;          // Size bytes of this header and the payload and stacks if any.  does NOT encode the size of the EventSize field itself.
        public int MetaDataId;          // a number identifying the description of this event.
        public int SequenceNumber;
        public long CaptureThreadId;
        public int CaptureProcNumber;
        public long ThreadId;
        public long TimeStamp;
        public Guid ActivityID;
        public Guid RelatedActivityID;
        public bool IsSorted;
        public int PayloadSize;         // size in bytes of the user defined payload data.
        public IntPtr Payload;
        public int StackId;
        public int StackBytesSize;
        public IntPtr StackBytes;
        public int HeaderSize;         // The size of the event up to the payload
        public int TotalNonHeaderSize; // The size of the payload, stack, and alignment padding


        public bool IsMetadata() => MetaDataId == 0; // 0 means that it's a metadata Id

        /// <summary>
        /// Size of the event header + stack + payload (includes EventSize field itself)
        /// </summary>
        public static int GetTotalEventSize(byte* headerPtr, int formatVersion)
        {
            if (formatVersion <= 3)
            {
                LayoutV3* header = (LayoutV3*)headerPtr;
                return header->EventSize + sizeof(int);
            }
            else //if(formatVersion == 4)
            {
                LayoutV4* header = (LayoutV4*)headerPtr;
                return header->EventSize + sizeof(int);
            }
        }

        /// <summary>
        /// Header Size is defined to be the number of bytes before the Payload bytes.
        /// </summary>
        public static int GetHeaderSize(int formatVersion)
        {
            if (formatVersion <= 3)
            {
                return sizeof(LayoutV3) - 4;
            }
            else //if(formatVersion == 4)
            {
                return sizeof(LayoutV4) - 4;
            }
        }

        public static Guid GetRelatedActivityID(byte* headerPtr)
        {
            LayoutV3* header = (LayoutV3*)headerPtr;
            return header->RelatedActivityID;
        }
    }
    #endregion
}
