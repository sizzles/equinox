﻿namespace Foldunk.EventStore

open EventStore.ClientAPI
open Foldunk
open FSharp.Control
open Serilog // NB must shadow EventStore.ClientAPI.ILogger
open System
open TypeShape

[<RequireQualifiedAccess>]
type Direction = Forward | Backward

module Log =
    [<NoEquality; NoComparison>]
    type Measurement = { stream: string; interval: StopwatchInterval; bytes: int; count: int }
    [<NoEquality; NoComparison>]
    type Event =
        | WriteSuccess of Measurement
        | WriteConflict of Measurement
        | Slice of Direction * Measurement
        | Batch of Direction * slices: int * Measurement
    let prop name value (log : ILogger) = log.ForContext(name, value)
    open Serilog.Events
    /// Attach a property to the log context to hold the metrics
    // Sidestep Log.ForContext converting to a string; see https://github.com/serilog/serilog/issues/1124
    let event (value : Event) (log : ILogger) =
        let enrich (e : LogEvent) = e.AddPropertyIfAbsent(LogEventProperty("esEvt", ScalarValue(value)))
        log.ForContext({ new Serilog.Core.ILogEventEnricher with member __.Enrich(evt,_) = enrich evt })
    let withLoggedRetries<'t> retryPolicy (contextLabel : string) (f : ILogger -> Async<'t>) log: Async<'t> =
        match retryPolicy with
        | None -> f log
        | Some retryPolicy ->
            let withLoggingContextWrapping count =
                let log = if count = 1 then log else log |> prop contextLabel count
                f log
            retryPolicy withLoggingContextWrapping
    let (|BlobLen|) = function null -> 0 | (x : byte[]) -> x.Length

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type EsSyncResult = Written of EventStore.ClientAPI.WriteResult | Conflict

module private Write =
    /// Yields `EsSyncResult.Written` or `EsSyncResult.Conflict` to signify WrongExpectedVersion
    let private writeEventsAsync (log : ILogger) (conn : IEventStoreConnection) (streamName : string) (version : int) (events : EventData[])
        : Async<EsSyncResult> = async {
        try
            let! wr = conn.AppendToStreamAsync(streamName, version, events) |> Async.AwaitTaskCorrect
            return EsSyncResult.Written wr
        with :? EventStore.ClientAPI.Exceptions.WrongExpectedVersionException as ex ->
            log.Information(ex, "Ges TrySync WrongExpectedVersionException")
            return EsSyncResult.Conflict }
    let eventDataBytes events =
        let eventDataLen (x : EventData) = match x.Data, x.Metadata with Log.BlobLen bytes, Log.BlobLen metaBytes -> bytes + metaBytes
        events |> Array.sumBy eventDataLen
    let private writeEventsLogged (conn : IEventStoreConnection) (streamName : string) (version : int) (events : EventData[]) (log : ILogger)
        : Async<EsSyncResult> = async {
        let bytes, count = eventDataBytes events, events.Length
        let log = log |> Log.prop "bytes" bytes
        let writeLog = log |> Log.prop "stream" streamName |> Log.prop "expectedVersion" version |> Log.prop "count" count
        let! t, result = writeEventsAsync writeLog conn streamName version events |> Stopwatch.Time
        let reqMetric : Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let resultLog, evt =
            match result, reqMetric with
            | EsSyncResult.Conflict, m -> log, Log.WriteConflict m
            | EsSyncResult.Written x, m ->
                log |> Log.prop "nextExpectedVersion" x.NextExpectedVersion |> Log.prop "logPosition" x.LogPosition,
                Log.WriteSuccess m
        // TODO drop expectedVersion when consumption no longer requires that literal; ditto stream when literal formatting no longer required
        (resultLog |> Log.event evt).Information("Ges{action:l} stream={stream} count={count} expectedVersion={expectedVersion} conflict={conflict}",
            "Write", streamName, events.Length, version, match evt with Log.WriteConflict _ -> true | _ -> false)
        return result }
    let writeEvents (log : ILogger) retryPolicy (conn : IEventStoreConnection) (streamName : string) (version : int) (events : EventData[])
        : Async<EsSyncResult> =
        let call = writeEventsLogged conn streamName version events
        Log.withLoggedRetries retryPolicy "writeAttempt" call log

module private Read =
    let private readSliceAsync (conn : IEventStoreConnection) (streamName : string) (direction : Direction) (batchSize : int) (startPos : int)
        : Async<StreamEventsSlice> = async {
        let call =
            match direction with
            | Direction.Forward ->  conn.ReadStreamEventsForwardAsync(streamName, startPos, batchSize, resolveLinkTos = false)
            | Direction.Backward -> conn.ReadStreamEventsBackwardAsync(streamName, startPos, batchSize, resolveLinkTos = false)
        return! call |> Async.AwaitTaskCorrect }
    let (|ResolvedEventLen|) (x : ResolvedEvent) = match x.Event.Data, x.Event.Metadata with Log.BlobLen bytes, Log.BlobLen metaBytes -> bytes + metaBytes
    let private loggedReadSlice conn streamName direction batchSize startPos (log : ILogger) : Async<StreamEventsSlice> = async {
        let! t, slice = readSliceAsync conn streamName direction batchSize startPos |> Stopwatch.Time
        let bytes, count = slice.Events |> Array.sumBy (|ResolvedEventLen|), slice.Events.Length
        let reqMetric : Log.Measurement ={ stream = streamName; interval = t; bytes = bytes; count = count}
        let evt = Log.Slice (direction, reqMetric)
        (log |> Log.prop "startPos" startPos |> Log.prop "bytes" bytes |> Log.event evt).Information(
            // TODO drop sliceLength, totalPayloadSize when consumption no longer requires that literal; ditto stream when literal formatting no longer required
            "Ges{action:l} stream={stream} count={count} version={version} sliceLength={sliceLength} totalPayloadSize={totalPayloadSize}",
            "Read", streamName, count, slice.LastEventNumber, batchSize, bytes)
        return slice }
    let private readBatches (log : ILogger) (readSlice : int -> ILogger -> Async<StreamEventsSlice>)
            (maxPermittedBatchReads : int option) (startPosition : int)
        : AsyncSeq<int option * ResolvedEvent[]> =
        let rec loop batchCount pos = asyncSeq {
            match maxPermittedBatchReads with
            | Some mpbr when batchCount >= mpbr -> log.Information "batch Limit exceeded"; invalidOp "batch Limit exceeded"
            | _ -> ()

            let batchLog = log |> Log.prop "batchIndex" batchCount
            let! slice = readSlice pos batchLog
            match slice.Status with
            | SliceReadStatus.StreamDeleted -> raise <| EventStore.ClientAPI.Exceptions.StreamDeletedException(slice.Stream)
            | SliceReadStatus.StreamNotFound -> yield Some ExpectedVersion.NoStream, Array.empty
            | SliceReadStatus.Success ->
                let version = if batchCount = 0 then Some slice.LastEventNumber else None
                yield version, slice.Events
                if not slice.IsEndOfStream then
                    yield! loop (batchCount + 1) slice.NextEventNumber
            | x -> raise <| System.ArgumentOutOfRangeException("SliceReadStatus", x, "Unknown result value") }
        loop 0 startPosition
    let resolvedEventBytes events = events |> Array.sumBy (|ResolvedEventLen|)
    let logBatchRead direction streamName t events batchSize version (log : ILogger) =
        let bytes, count = resolvedEventBytes events, events.Length
        let reqMetric : Log.Measurement = { stream = streamName; interval = t; bytes = bytes; count = count}
        let batches = (events.Length - 1)/batchSize + 1
        let action = match direction with Direction.Forward -> "LoadF" | Direction.Backward -> "LoadB"
        let evt = Log.Event.Batch (direction, batches, reqMetric)
        (log |> Log.prop "bytes" bytes |> Log.event evt).Information(
            "Ges{action:l} stream={stream} count={count}/{batches} version={version}",
            action, streamName, count, batches, version)
    let loadForwardsFrom (log : ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName startPosition
        : Async<int * ResolvedEvent[]> = async {
        let mergeBatches (batches: AsyncSeq<int option * ResolvedEvent[]>) = async {
            let mutable versionFromStream = None
            let! (events : ResolvedEvent[]) =
                batches
                |> AsyncSeq.map (function None, events -> events | (Some _) as reportedVersion, events -> versionFromStream <- reportedVersion; events)
                |> AsyncSeq.concatSeq
                |> AsyncSeq.toArrayAsync
            let version = match versionFromStream with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, events }
        let call pos = loggedReadSlice conn streamName Direction.Forward batchSize pos
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let direction = Direction.Forward
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "direction" direction |> Log.prop "stream" streamName
        let batches : AsyncSeq<int option * ResolvedEvent[]> = readBatches log retryingLoggingReadSlice maxPermittedBatchReads startPosition
        let! t, (version, events) = mergeBatches batches |> Stopwatch.Time
        log |> logBatchRead direction streamName t events batchSize version
        return version, events }
    let partitionPayloadFrom firstUsedEventNumber : ResolvedEvent[] -> int * int =
        let acc (tu,tr) ((ResolvedEventLen bytes) as y) = if y.Event.EventNumber < firstUsedEventNumber then tu, tr + bytes else tu + bytes, tr
        Array.fold acc (0,0)
    let loadBackwardsUntilCompactionOrStart (log : ILogger) retryPolicy conn batchSize maxPermittedBatchReads streamName isCompactionEvent
        : Async<int * ResolvedEvent[]> = async {
        let mergeFromCompactionPointOrStartFromBackwardsStream (log : ILogger) (batchesBackward : AsyncSeq<int option * ResolvedEvent[]>)
            : Async<int * ResolvedEvent[]> = async {
            let versionFromStream, lastBatch = ref None, ref None
            let! tempBackward =
                batchesBackward
                |> AsyncSeq.map (function
                    | None, events -> lastBatch := Some events; events
                    | (Some _) as reportedVersion, events -> versionFromStream := reportedVersion; lastBatch := Some events; events)
                |> AsyncSeq.concatSeq
                |> AsyncSeq.takeWhileInclusive (fun x ->
                    if not (isCompactionEvent x) then true // continue the search
                    else
                        match !lastBatch with
                        | None -> log.Information("GesStop stream={stream} at={eventNumber}", streamName, x.Event.EventNumber)
                        | Some batch ->
                            let used, residual = batch |> partitionPayloadFrom x.Event.EventNumber
                            log.Information("GesStop stream={stream} at={eventNumber} used={used} residual={residual}", streamName, x.Event.EventNumber, used, residual)
                        false)
                |> AsyncSeq.toArrayAsync
            let eventsForward = Array.Reverse(tempBackward); tempBackward // sic - relatively cheap, in-place reverse of something we own
            let version = match !versionFromStream with Some version -> version | None -> invalidOp "no version encountered in event batch stream"
            return version, eventsForward }
        let call pos = loggedReadSlice conn streamName Direction.Backward batchSize pos
        let retryingLoggingReadSlice pos = Log.withLoggedRetries retryPolicy "readAttempt" (call pos)
        let log = log |> Log.prop "batchSize" batchSize |> Log.prop "stream" streamName
        let startPosition = StreamPosition.End
        let direction = Direction.Backward
        let readlog = log |> Log.prop "direction" direction
        let batchesBackward : AsyncSeq<int option * ResolvedEvent[]> = readBatches readlog retryingLoggingReadSlice maxPermittedBatchReads startPosition
        let! t, (version, events) = mergeFromCompactionPointOrStartFromBackwardsStream log batchesBackward |> Stopwatch.Time
        log |> logBatchRead direction streamName t events batchSize version
        return version, events }

module EventSumAdapters =
    let private encodedEventOfResolvedEvent (x : ResolvedEvent) : EventSum.EncodedEvent<byte[]> =
        { EventType = x.Event.EventType; Payload = x.Event.Data }
    let private eventDataOfEncodedEvent (x : EventSum.EncodedEvent<byte[]>) =
        EventData(Guid.NewGuid(), x.EventType, (*isJson*) true, x.Payload, [||])
    let encodeEvents (codec : EventSum.IEventSumEncoder<'event, byte[]>) (xs : 'event seq) : EventData[] =
        xs |> Seq.map (codec.Encode >> eventDataOfEncodedEvent) |> Seq.toArray
    let decodeKnownEvents (codec : EventSum.IEventSumEncoder<'event, byte[]>) (xs : ResolvedEvent[]) : 'event seq =
        xs |> Seq.map encodedEventOfResolvedEvent |> Seq.choose codec.TryDecode

type Token = { streamVersion: int; compactionEventNumber: int option }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Token =
    let private create compactionEventNumber batchCapacityLimit streamVersion : Storage.StreamToken =
        { value = box { streamVersion = streamVersion; compactionEventNumber = compactionEventNumber }; batchCapacityLimit = batchCapacityLimit }
    /// No batching / compaction; we only need to retain the StreamVersion
    let ofNonCompacting streamVersion : Storage.StreamToken =
        create None None streamVersion
    // headroom before compaction is necessary given the stated knowledge of the last (if known) `compactionEventNumberOption`
    let private batchCapacityLimit compactedEventNumberOption unstoredEventsPending batchSize streamVersion =
        match compactedEventNumberOption with
        | Some compactionEventNumber -> (batchSize - unstoredEventsPending) - (streamVersion - compactionEventNumber + 1) |> max 0
        | None -> (batchSize - unstoredEventsPending) - (streamVersion + 1) - 1 |> max 0
    let (*private*) ofCompactionEventNumber compactedEventNumberOption unstoredEventsPending batchSize streamVersion : Storage.StreamToken =
        let batchCapacityLimit = batchCapacityLimit compactedEventNumberOption unstoredEventsPending batchSize streamVersion
        create compactedEventNumberOption (Some batchCapacityLimit) streamVersion
    /// Assume we have not seen any compaction events; use the batchSize and version to infer headroom
    let ofUncompactedVersion batchSize streamVersion : Storage.StreamToken =
        ofCompactionEventNumber None 0 batchSize streamVersion
    /// Use previousToken plus the data we are adding and the position we are adding it to infer a headroom
    let ofPreviousTokenAndEventsLength (previousToken : Storage.StreamToken) eventsLength batchSize streamVersion : Storage.StreamToken =
        let compactedEventNumber = (unbox previousToken.value).compactionEventNumber
        ofCompactionEventNumber compactedEventNumber eventsLength batchSize streamVersion
    /// Use an event just read from the stream to infer headroom
    let ofCompactionResolvedEventAndVersion (compactionEvent: ResolvedEvent) batchSize streamVersion : Storage.StreamToken =
        ofCompactionEventNumber (Some compactionEvent.Event.EventNumber) 0 batchSize streamVersion
    /// Use an event we are about to write to the stream to infer headroom
    let ofPreviousStreamVersionAndCompactionEventDataIndex prevStreamVersion compactionEventDataIndex eventsLength batchSize streamVersion' : Storage.StreamToken =
        ofCompactionEventNumber (Some (prevStreamVersion + 1 + compactionEventDataIndex)) eventsLength batchSize streamVersion'

type GesConnection(connection, ?readRetryPolicy, ?writeRetryPolicy) =
    member __.Connection = connection
    member __.ReadRetryPolicy = readRetryPolicy
    member __.WriteRetryPolicy = writeRetryPolicy

type GesBatchingPolicy(getMaxBatchSize : unit -> int, ?batchCountLimit) =
    new (maxBatchSize) = GesBatchingPolicy(fun () -> maxBatchSize)
    member __.BatchSize = getMaxBatchSize()
    member __.MaxBatches = batchCountLimit

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type GatewaySyncResult = Written of Storage.StreamToken | Conflict

type GesGateway(conn : GesConnection, batching : GesBatchingPolicy) =
    let isResolvedEventEventType predicate (x:ResolvedEvent) = predicate x.Event.EventType
    let tryIsResolvedEventEventType predicateOption = predicateOption |> Option.map isResolvedEventEventType
    member __.LoadBatched streamName log isCompactionEventType: Async<Storage.StreamToken * ResolvedEvent[]> = async {
        let! version, events = Read.loadForwardsFrom log conn.ReadRetryPolicy conn.Connection batching.BatchSize batching.MaxBatches streamName 0
        match tryIsResolvedEventEventType isCompactionEventType with
        | None -> return Token.ofNonCompacting version, events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack isCompactionEvent with
            | None -> return Token.ofUncompactedVersion batching.BatchSize version, events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, events }
    member __.LoadBackwardsStoppingAtCompactionEvent streamName log isCompactionEventType: Async<Storage.StreamToken * ResolvedEvent[]> = async {
        let isCompactionEvent = isResolvedEventEventType isCompactionEventType
        let! version, events =
            Read.loadBackwardsUntilCompactionOrStart log conn.ReadRetryPolicy conn.Connection batching.BatchSize batching.MaxBatches streamName isCompactionEvent
        match Array.tryHead events |> Option.filter isCompactionEvent with
        | None -> return Token.ofUncompactedVersion batching.BatchSize version, events
        | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, events }
    member __.LoadFromToken streamName log (token : Storage.StreamToken) isCompactionEventType
        : Async<Storage.StreamToken * ResolvedEvent[]> = async {
        let streamPosition = (unbox token.value).streamVersion + 1
        let! version, events = Read.loadForwardsFrom log conn.ReadRetryPolicy conn.Connection batching.BatchSize batching.MaxBatches streamName streamPosition
        match tryIsResolvedEventEventType isCompactionEventType with
        | None -> return Token.ofNonCompacting version, events
        | Some isCompactionEvent ->
            match events |> Array.tryFindBack isCompactionEvent with
            | None -> return Token.ofPreviousTokenAndEventsLength token events.Length batching.BatchSize version, events
            | Some resolvedEvent -> return Token.ofCompactionResolvedEventAndVersion resolvedEvent batching.BatchSize version, events }
    member __.TrySync streamName log (token : Storage.StreamToken) (encodedEvents: EventData array) isCompactionEventType : Async<GatewaySyncResult> = async {
        let streamVersion = (unbox token.value).streamVersion
        let! wr = Write.writeEvents log conn.WriteRetryPolicy conn.Connection streamName streamVersion encodedEvents
        match wr with
        | EsSyncResult.Conflict -> return GatewaySyncResult.Conflict
        | EsSyncResult.Written wr ->

        let version' = wr.NextExpectedVersion
        let token =
            match isCompactionEventType with
            | None -> Token.ofNonCompacting version'
            | Some isCompactionEvent ->
                let isEventDataEventType predicate (x:EventData) = predicate x.Type
                match encodedEvents |> Array.tryFindIndexBack (isEventDataEventType isCompactionEvent) with
                | None -> Token.ofPreviousTokenAndEventsLength token encodedEvents.Length batching.BatchSize version'
                | Some compactionEventIndex ->
                    Token.ofPreviousStreamVersionAndCompactionEventDataIndex streamVersion compactionEventIndex encodedEvents.Length batching.BatchSize version'
        return GatewaySyncResult.Written token }

type GesCategory<'event, 'state>(gateway : GesGateway, codec : EventSum.IEventSumEncoder<'event, byte[]>, ?compactionStrategy) =
    let loadAlgorithm load streamName initial log =
        let batched = load initial (gateway.LoadBatched streamName log None)
        let compacted predicate = load initial (gateway.LoadBackwardsStoppingAtCompactionEvent streamName log predicate)
        match compactionStrategy with
        | Some predicate -> compacted predicate
        | None -> batched
    let load (fold: 'state -> 'event seq -> 'state) initial f = async {
        let! token, events = f
        return token, fold initial (EventSumAdapters.decodeKnownEvents codec events) }
    member __.Load (fold: 'state -> 'event seq -> 'state) (initial: 'state) (streamName : string) (log : ILogger) : Async<Storage.StreamToken * 'state> =
        loadAlgorithm (load fold) streamName initial log
    member __.LoadFromToken (fold: 'state -> 'event seq -> 'state) (state: 'state) (streamName : string) token (log : ILogger) : Async<Storage.StreamToken * 'state> =
        (load fold) state (gateway.LoadFromToken streamName log token compactionStrategy)
    member __.TrySync (fold: 'state -> 'event seq -> 'state) streamName (log : ILogger) (token, state) (events : 'event list) : Async<Storage.SyncResult<'state>> = async {
        let encodedEvents : EventData[] = EventSumAdapters.encodeEvents codec (Seq.ofList events)
        let! syncRes = gateway.TrySync streamName log token encodedEvents compactionStrategy
        match syncRes with
        | GatewaySyncResult.Conflict ->         return Storage.SyncResult.Conflict  (load fold state (gateway.LoadFromToken streamName log token compactionStrategy))
        | GatewaySyncResult.Written token' ->   return Storage.SyncResult.Written   (token', fold state (Seq.ofList events)) }

type GesFolder<'event, 'state>(category : GesCategory<'event, 'state>, fold: 'state -> 'event seq -> 'state, initial: 'state) =
    interface ICategory<'event, 'state> with
        member __.Load (streamName : string) (log : ILogger) : Async<Storage.StreamToken * 'state> =
            category.Load fold initial streamName log
        member __.TrySync streamName (log : ILogger) (token, state) (events : 'event list) : Async<Storage.SyncResult<'state>> = async {
            let! syncRes = category.TrySync fold streamName log (token, state) events
            match syncRes with
            | Storage.SyncResult.Conflict resync ->         return Storage.SyncResult.Conflict resync
            | Storage.SyncResult.Written (token',state') -> return Storage.SyncResult.Written (token',state') }

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type CompactionStrategy =
    | EventType of string
    | Predicate of (string -> bool)

type GesStreamBuilder<'event, 'state>(gateway, codec, fold, initial, ?compaction, ?initialTokenAndState) =
    member __.Create streamName : Foldunk.IStream<'event, 'state> =
        let compactionPredicateOption =
            match compaction with
            | None -> None
            | Some (CompactionStrategy.Predicate predicate) -> Some predicate
            | Some (CompactionStrategy.EventType eventType) -> Some (fun x -> x = eventType)
        let gesCategory = GesCategory<'event, 'state>(gateway, codec, ?compactionStrategy = compactionPredicateOption)

        let folder = GesFolder<'event, 'state>(gesCategory, fold, initial)

        let category : ICategory<_,_> = folder :> _

        Foldunk.Stream<'event, 'state>(category, streamName, ?initialTokenAndState = initialTokenAndState) :> _