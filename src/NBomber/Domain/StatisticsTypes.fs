module internal NBomber.Domain.StatisticsTypes

open System
open System.Collections.Generic

open HdrHistogram.Iteration
open Nessos.Streams

open NBomber.Contracts
open NBomber.Domain.DomainTypes

//type DataTransferCount = {
//    MinKb: float<kb>
//    MeanKb: float<kb>
//    MaxKb: float<kb>
//    AllMB: float<mb>
//}

//type RawStepResults = {
//    StepName: string
//    OkCount: int
//    FailCount: int
//    Errors: Stream<ErrorStats>
//    Latencies: Stream<HistogramIterationValue>
//    MinMicroSec: int
//    MaxMicroSec: int
//    DataTransfer: DataTransferCount
//    Created: TimeSpan
//}

//type RawStepStats = {
//    StepName: string
//    RequestCount: int
//    OkCount: int
//    FailCount: int
//    RPS: int
//    Min: Latency
//    Mean: Latency
//    Max: Latency
//    Percent50: Latency
//    Percent75: Latency
//    Percent95: Latency
//    Percent99: Latency
//    StdDev: int
//    DataTransfer: DataTransferCount
//    ErrorStats: Stream<ErrorStats>
//}

//type RawScenarioStats = {
//    ScenarioName: string
//    RequestCount: int
//    OkCount: int
//    FailCount: int
//    AllDataMB: float
//    RawStepsStats: Stream<RawStepStats>
//    LatencyCount: LatencyCount
//    LoadSimulationStats: LoadSimulationStats
//    Duration: TimeSpan
//    ErrorStats: Stream<ErrorStats>
//}
