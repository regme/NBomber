module internal NBomber.Domain.DomainTypes

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

open HdrHistogram

open Serilog

open NBomber.Contracts
open NBomber.Domain.ConnectionPool
open NBomber.Extensions.InternalExtensions

[<Measure>] type microSec
[<Measure>] type ms
[<Measure>] type bytes
[<Measure>] type kb
[<Measure>] type mb

//todo: use opaque types
type StepName = string
type ScenarioName = string
type Latency = int

type StopCommand =
    | StopScenario of ScenarioName * reason:string
    | StopTest of reason:string

type UntypedStepContext = {
    CorrelationId: CorrelationId
    CancellationToken: CancellationToken
    Connection: obj
    Logger: ILogger
    mutable FeedItem: obj
    mutable Data: Dict<string,obj>
    mutable InvocationCount: int
    StopScenario: string * string -> unit // scenarioName * reason
    StopCurrentTest: string -> unit       // reason
}

type StepExecution =
    | SyncExec  of (UntypedStepContext -> Response)
    | AsyncExec of (UntypedStepContext -> Task<Response>)

type Step = {
    StepName: StepName
    ConnectionPoolArgs: ConnectionPoolArgs<obj> option
    ConnectionPool: ConnectionPool option
    Execute: StepExecution
    Feed: IFeed<obj> option
    DoNotTrack: bool
} with
    interface IStep with
        member this.StepName = this.StepName
        member this.DoNotTrack = this.DoNotTrack

type StepExecutionData = {
    mutable OkCount: int
    mutable FailCount: int
    Errors: Dictionary<int, ErrorStats>
    LatenciesMicroSec: LongHistogram
    mutable MinMicroSec: float<microSec>
    mutable MaxMicroSec: float<microSec>
    mutable Less800: int
    mutable More800Less1200: int
    mutable More1200: int
    DataTransferBytes: LongHistogram
    mutable MinBytes: float<bytes>
    mutable MaxBytes: float<bytes>
    mutable AllMB: float<mb>
}

type RunningStep = {
    Value: Step
    Context: UntypedStepContext
    ExecutionData: StepExecutionData
}

[<Struct>]
type StepResponse = {
    Response: Response
    StartTimeMs: float<ms>
    LatencyMicroSec: float<microSec>
}

type LoadTimeSegment = {
    StartTime: TimeSpan
    EndTime: TimeSpan
    Duration: TimeSpan
    PrevSegmentCopiesCount: int
    LoadSimulation: LoadSimulation
}

type LoadTimeLine = LoadTimeSegment list

type Scenario = {
    ScenarioName: ScenarioName
    Init: (IScenarioContext -> Task) option
    Clean: (IScenarioContext -> Task) option
    Steps: Step list
    LoadTimeLine: LoadTimeLine
    WarmUpDuration: TimeSpan
    PlanedDuration: TimeSpan
    ExecutedDuration: TimeSpan option
    CustomSettings: string
    GetStepsOrder: unit -> int[]
}
