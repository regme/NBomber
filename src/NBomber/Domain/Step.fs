[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal NBomber.Domain.Step

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

open Serilog
open FSharp.UMX
open FSharp.Control.Tasks.NonAffine
open HdrHistogram

open NBomber
open NBomber.Extensions.InternalExtensions
open NBomber.Contracts
open NBomber.Domain.DomainTypes
open NBomber.Domain.ConnectionPool
open NBomber.Domain.Statistics

//todo: add max duration to filter extra responses
type StepDep = {
    ScenarioName: string
    Logger: ILogger
    CancellationToken: CancellationToken
    GlobalTimer: Stopwatch
    CorrelationId: CorrelationId
    ExecStopCommand: StopCommand -> unit
}

module StepContext =

    let create (dep: StepDep) (step: Step) =

        let getConnection (pool: ConnectionPool option) =
            match pool with
            | Some v ->
                let index = dep.CorrelationId.CopyNumber % v.AliveConnections.Length
                v.AliveConnections.[index]

            | None -> Unchecked.defaultof<_>

        { CorrelationId = dep.CorrelationId
          CancellationToken = dep.CancellationToken
          Connection = getConnection step.ConnectionPool
          Logger = dep.Logger
          FeedItem = Unchecked.defaultof<_>
          Data = Dict.empty
          InvocationCount = 0
          StopScenario = fun (scnName,reason) -> StopScenario(scnName, reason) |> dep.ExecStopCommand
          StopCurrentTest = fun reason -> StopTest(reason) |> dep.ExecStopCommand }

module StepExecutionData =

    let empty = {
        OkCount = 0
        FailCount = 0
        Errors = Dictionary<int, ErrorStats>()
        LatenciesMicroSec = LongHistogram(TimeStamp.Hours(1), 3)
        MinMicroSec = 0.0<microSec>
        MaxMicroSec = 0.0<microSec>
        Less800 = 0
        More800Less1200 = 0
        More1200 = 0
        DataTransferBytes = LongHistogram(TimeStamp.Hours(1), 3)
        MinBytes = 0.0<bytes>
        MaxBytes = 0.0<bytes>
        AllMB = 0.0<mb>
    }

module RunningStep =

    let create (dep: StepDep) (step: Step) =
        { Value = step; Context = StepContext.create dep step; ExecutionData = StepExecutionData.empty }

    let updateContext (step: RunningStep) (data: Dict<string,obj>) =
        let context = step.Context

        let feedItem =
            match step.Value.Feed with
            | Some feed -> feed.GetNextItem(context.CorrelationId, data)
            | None      -> Unchecked.defaultof<_>

        context.InvocationCount <- context.InvocationCount + 1
        context.Data <- data
        context.FeedItem <- feedItem
        step

    let addResponse (step: RunningStep) (response: Response) =

        let data = step.ExecutionData

        let addErrorResponse (res: Response) =
            data.FailCount <- data.FailCount + 1
            match data.Errors.TryGetValue res.ErrorCode with
            | true, errorStats ->
                data.Errors.[res.ErrorCode] <- { errorStats with Count = errorStats.Count + 1 }
            | false, _ ->
                data.Errors.[res.ErrorCode] <- { ErrorCode = res.ErrorCode
                                                 Message = res.Exception.Value.Message
                                                 Count = 1 }
        match response.Exception with
        | Some ex -> addErrorResponse(response)
        | None    ->
            let latencyMicroSec = response.LatencyMs |> UMX.tag |> Converter.fromMsToMicroSec
            let latencyMs = Converter.fromMicroSecToMs latencyMicroSec
            let responseSize = response.SizeBytes |> float |> UMX.tag<bytes>

            data.OkCount <- data.OkCount + 1
            data.LatenciesMicroSec.RecordValue(int64 latencyMicroSec)
            data.MinMicroSec <- Statistics.min data.MinMicroSec latencyMicroSec
            data.MaxMicroSec <- Statistics.max data.MinMicroSec latencyMicroSec

            if latencyMs < 800.0<ms> then data.Less800 <- data.Less800 + 1
            if latencyMs > 800.0<ms> && latencyMs < 1200.0<ms> then data.More800Less1200 <- data.More800Less1200 + 1
            if latencyMs > 1200.0<ms> then data.More1200 <- data.More1200 + 1

            data.DataTransferBytes.RecordValue(int64 response.SizeBytes)
            data.MinBytes <- Statistics.min data.MinBytes responseSize
            data.MaxBytes <- Statistics.max data.MaxBytes responseSize
            data.AllMB <- data.AllMB + Statistics.Converter.fromBytesToMB responseSize

        step

let toUntypedExecute (execute: IStepContext<'TConnection,'TFeedItem> -> Response) =

    fun (untypedCtx: UntypedStepContext) ->

        let typedCtx = {
            new IStepContext<'TConnection,'TFeedItem> with
                member _.CorrelationId = untypedCtx.CorrelationId
                member _.CancellationToken = untypedCtx.CancellationToken
                member _.Connection = untypedCtx.Connection :?> 'TConnection
                member _.Data = untypedCtx.Data
                member _.FeedItem = untypedCtx.FeedItem :?> 'TFeedItem
                member _.Logger = untypedCtx.Logger
                member _.InvocationCount = untypedCtx.InvocationCount
                member _.StopScenario(scenarioName, reason) = untypedCtx.StopScenario(scenarioName, reason)
                member _.StopCurrentTest(reason) = untypedCtx.StopCurrentTest(reason)

                member _.GetPreviousStepResponse() =
                    try
                        let prevStepResponse = untypedCtx.Data.[Constants.StepResponseKey]
                        if isNull prevStepResponse then
                            Unchecked.defaultof<'T>
                        else
                            prevStepResponse :?> 'T
                    with
                    | ex -> Unchecked.defaultof<'T>
        }

        execute typedCtx

let toUntypedExecuteAsync (execute: IStepContext<'TConnection,'TFeedItem> -> Task<Response>) =

    fun (untypedCtx: UntypedStepContext) ->

        let typedCtx = {
            new IStepContext<'TConnection,'TFeedItem> with
                member _.CorrelationId = untypedCtx.CorrelationId
                member _.CancellationToken = untypedCtx.CancellationToken
                member _.Connection = untypedCtx.Connection :?> 'TConnection
                member _.Data = untypedCtx.Data
                member _.FeedItem = untypedCtx.FeedItem :?> 'TFeedItem
                member _.Logger = untypedCtx.Logger
                member _.InvocationCount = untypedCtx.InvocationCount
                member _.StopScenario(scenarioName, reason) = untypedCtx.StopScenario(scenarioName, reason)
                member _.StopCurrentTest(reason) = untypedCtx.StopCurrentTest(reason)

                member _.GetPreviousStepResponse() =
                    try
                        let prevStepResponse = untypedCtx.Data.[Constants.StepResponseKey]
                        if isNull prevStepResponse then
                            Unchecked.defaultof<'T>
                        else
                            prevStepResponse :?> 'T
                    with
                    | ex -> Unchecked.defaultof<'T>
        }

        execute typedCtx

// todo: check TaskCanceledException why it's OK??? maybe add IsCancel via context.Dep requested
let execStep (step: RunningStep) (globalTimer: Stopwatch) =
    let startTime = globalTimer.Elapsed
    try
        let resp =
            match step.Value.Execute with
            | SyncExec exec  -> exec step.Context
            | AsyncExec exec -> (exec step.Context).Result

        let latency =
            if resp.LatencyMs > 0.0 then Converter.fromMsToMicroSec(% resp.LatencyMs)
            else
                let endTime = globalTimer.Elapsed - startTime
                endTime.Ticks |> Statistics.Converter.fromTicksToMicroSec

        { Response = resp; StartTimeMs = UMX.tag startTime.TotalMilliseconds; LatencyMicroSec = latency }
    with
    | :? TaskCanceledException
    | :? OperationCanceledException ->
        { Response = Response.ok(); StartTimeMs = -1.0<ms>; LatencyMicroSec = -1.0<microSec> }

    | ex -> { Response = Response.fail(ex); StartTimeMs = -1.0<ms>; LatencyMicroSec = -1.0<microSec> }

// todo: check TaskCanceledException why it's OK??? maybe add IsCancel via context.Dep requested
let execStepAsync (step: RunningStep) (globalTimer: Stopwatch) = task {
    let startTime = globalTimer.Elapsed
    try
        let! resp =
            match step.Value.Execute with
            | SyncExec exec  -> Task.FromResult(exec step.Context)
            | AsyncExec exec -> exec step.Context

        let latency =
            if resp.LatencyMs > 0.0 then Converter.fromMsToMicroSec(% resp.LatencyMs)
            else
                let endTime = globalTimer.Elapsed - startTime
                endTime.Ticks |> Statistics.Converter.fromTicksToMicroSec

        return { Response = resp; StartTimeMs = UMX.tag startTime.TotalMilliseconds; LatencyMicroSec = latency }
    with
    | :? TaskCanceledException
    | :? OperationCanceledException ->
        return { Response = Response.ok(); StartTimeMs = -1.0<ms>; LatencyMicroSec = -1.0<microSec> }

    | ex -> return { Response = Response.fail(ex); StartTimeMs = -1.0<ms>; LatencyMicroSec = -1.0<microSec> }
}

let execSteps (dep: StepDep) (steps: RunningStep[]) (stepsOrder: int[]) =

    let data = Dict.empty
    let mutable skipStep = false

    for stepIndex in stepsOrder do
        if not skipStep && not dep.CancellationToken.IsCancellationRequested then
            try
                let mutable step = RunningStep.updateContext steps.[stepIndex] data
                let response = execStep step dep.GlobalTimer

                let payload = response.Response.Payload

                if not dep.CancellationToken.IsCancellationRequested && not step.Value.DoNotTrack then
                    response.Response.Payload <- null
                    step <- RunningStep.addResponse step response.Response

                if response.Response.Exception.IsNone then
                    data.[Constants.StepResponseKey] <- payload
                else
                    dep.Logger.Error(response.Response.Exception.Value, "Step '{StepName}' from scenario '{ScenarioName}' has failed. ", step.Value.StepName, dep.ScenarioName)
                    skipStep <- true
            with
            | ex -> dep.Logger.Fatal(ex, "Step with index '{0}' from scenario '{ScenarioName}' has failed.", stepIndex, dep.ScenarioName)

let execStepsAsync (dep: StepDep) (steps: RunningStep[]) (stepsOrder: int[]) = task {

    let data = Dict.empty
    let mutable skipStep = false

    for stepIndex in stepsOrder do
        if not skipStep && not dep.CancellationToken.IsCancellationRequested then
            try
                let mutable step = RunningStep.updateContext steps.[stepIndex] data
                let! response = execStepAsync step dep.GlobalTimer

                let payload = response.Response.Payload

                if not dep.CancellationToken.IsCancellationRequested && not step.Value.DoNotTrack then
                    response.Response.Payload <- null
                    step <- RunningStep.addResponse step response.Response

                if response.Response.Exception.IsNone then
                    data.[Constants.StepResponseKey] <- payload
                else
                    dep.Logger.Error(response.Response.Exception.Value, "Step '{StepName}' from scenario '{ScenarioName}' has failed. ", step.Value.StepName, dep.ScenarioName)
                    skipStep <- true
            with
            | ex -> dep.Logger.Fatal(ex, "Step with index '{0}' from scenario '{ScenarioName}' has failed.", stepIndex, dep.ScenarioName)
}

//let filterByDuration (duration: TimeSpan) (stepResponses: Stream<StepResponse>) =
//    let validEndTime (endTime) = endTime <= duration.TotalMilliseconds
//    let createEndTime (response) = response.StartTimeMs + float response.LatencyMicroSec
//
//    stepResponses
//    |> Stream.filter(fun x -> x.StartTimeMs <> -1.0) // to filter out TaskCanceledException
//    |> Stream.choose(fun x ->
//        match x |> createEndTime |> validEndTime with
//        | true  -> Some x
//        | false -> None)

let isAllExecSync (steps: Step list) =
    steps
    |> List.map(fun x -> x.Execute)
    |> List.forall(function SyncExec _ -> true | AsyncExec _ -> false)

