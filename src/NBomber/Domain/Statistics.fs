module internal NBomber.Domain.Statistics

open System
open System.Data

open HdrHistogram
open Nessos.Streams
open FSharp.UMX

open NBomber.Extensions.InternalExtensions
open NBomber.Contracts
open NBomber.Domain.DomainTypes

module Converter =

    [<Literal>]
    let TicksPerMicrosecond = 10.0

    let inline fromBytesToKb (sizeBytes: float<bytes>) =
        if sizeBytes > 0.0<bytes> then UMX.tag<kb>(float sizeBytes / 1024.0)
        else 0.0<kb>

    let inline fromKbToMB (sizeKb: float<kb>) =
        if sizeKb > 0.0<kb> then UMX.tag<mb>(sizeKb / 1024.0<kb>)
        else 0.0<mb>

    let fromBytesToMB = fromBytesToKb >> fromKbToMB

    let inline fromTicksToMicroSec (ticks: int64) = UMX.tag<microSec>(float(ticks) / TicksPerMicrosecond)
    let inline fromMicroSecToMs (value: float<microSec>) = UMX.tag<ms>(% value / 1000.0)
    let inline fromMsToMicroSec (value: float<ms>) = UMX.tag<microSec>(% value * 1000.0)

    let roundResult (value: float) =
        let result = Math.Round(value, 2)
        if result > 0.01 then result
        else Math.Round(value, 4)
        |> UMX.tag

let inline min a b = if a < b then a else b
let inline max a b = if a > b then a else b

let calcRPS (requestCount: int) (executionTime: TimeSpan) =
    let totalSec = if executionTime.TotalSeconds < 1.0 then 1.0
                   else executionTime.TotalSeconds

    float requestCount / totalSec

module ErrorStats =

    let merge (stepStats: Stream<StepStats>) =
        stepStats
        |> Stream.collect(fun x -> x.ErrorStats |> Stream.ofArray)
        |> Stream.groupBy(fun x -> x.ErrorCode)
        |> Stream.map(fun (code,errorStats) ->
            { ErrorCode = code
              Message = errorStats |> Seq.head |> fun x -> x.Message
              Count = errorStats |> Seq.sumBy(fun x -> x.Count) }
        )

module StepStats =

    let inline private fromMicroSecToMs (value: float) = value |> UMX.tag |> Converter.fromMicroSecToMs |> UMX.untag

    let create (stepName: string) (stepData: StepExecutionData) (duration: TimeSpan) =
        let requestCount = stepData.OkCount + stepData.FailCount

        let latencies =
            if stepData.LatenciesMicroSec.TotalCount > 0L then ValueSome(stepData.LatenciesMicroSec.Copy())
            else ValueNone

        let dataTransfer =
            if stepData.DataTransferBytes.TotalCount > 0L then ValueSome(stepData.DataTransferBytes.Copy())
            else ValueNone

        { StepName = stepName
          RequestCount = requestCount
          OkCount = stepData.OkCount
          FailCount = stepData.FailCount
          Min = stepData.MinMicroSec |> Converter.fromMicroSecToMs |> UMX.untag

          Mean = latencies
                 |> ValueOption.map(fun x -> x.GetMean() |> UMX.tag |> Converter.fromMicroSecToMs |> UMX.untag)
                 |> ValueOption.defaultValue 0.0

          Max = stepData.MaxMicroSec |> Converter.fromMicroSecToMs |> UMX.untag
          RPS = calcRPS stepData.RequestLessSecCount duration

          Percent50 = latencies
                      |> ValueOption.map(fun x -> x.GetValueAtPercentile(50.0) |> float |> fromMicroSecToMs)
                      |> ValueOption.defaultValue 0.0

          Percent75 = latencies
                      |> ValueOption.map(fun x -> x.GetValueAtPercentile(75.0) |> float |> fromMicroSecToMs)
                      |> ValueOption.defaultValue 0.0

          Percent95 = latencies
                      |> ValueOption.map(fun x -> x.GetValueAtPercentile(95.0) |> float |> fromMicroSecToMs)
                      |> ValueOption.defaultValue 0.0

          Percent99 = latencies
                      |> ValueOption.map(fun x -> x.GetValueAtPercentile(50.0) |> float |> fromMicroSecToMs)
                      |> ValueOption.defaultValue 0.0

          StdDev = latencies
                   |> ValueOption.map(fun x -> x.GetStdDeviation() |> fromMicroSecToMs)
                   |> ValueOption.defaultValue 0.0

          LatencyCount = { Less800 = stepData.Less800; More800Less1200 = stepData.More800Less1200; More1200 = stepData.More1200 }
          MinDataKb = stepData.MinBytes |> Converter.fromBytesToKb |> UMX.untag

          MeanDataKb = dataTransfer
                       |> ValueOption.map(fun x -> x.GetMean() |> UMX.tag |> Converter.fromBytesToKb |> UMX.untag)
                       |> ValueOption.defaultValue 0.0

          MaxDataKb = stepData.MaxBytes |> Converter.fromBytesToKb |> UMX.untag
          AllDataMB = % stepData.AllMB
          ErrorStats = stepData.Errors.Values |> Stream.ofSeq |> Stream.toArray } // we use Stream for safe enumeration

    let merge (stepsStats: Stream<StepStats>) =
        stepsStats
        |> Stream.groupBy(fun x -> x.StepName)
        |> Stream.map(fun (name, stats) ->
            let statsStream = stats |> Stream.ofSeq
            let requestCount = statsStream |> Stream.sumBy(fun x -> x.RequestCount)
            let rps = statsStream |> Stream.sumBy(fun x -> x.RPS)
            let less800 = statsStream |> Stream.sumBy(fun x -> x.LatencyCount.Less800)
            let more800Less1200 = statsStream |> Stream.sumBy(fun x -> x.LatencyCount.More800Less1200)
            let more1200 = statsStream |> Stream.sumBy(fun x -> x.LatencyCount.More1200)
            let errorStats = ErrorStats.merge statsStream

            { StepName = name
              RequestCount = requestCount
              OkCount = statsStream |> Stream.sumBy(fun x -> x.OkCount)
              FailCount = statsStream |> Stream.sumBy(fun x -> x.FailCount)
              Min = statsStream |> Stream.map(fun x -> % x.Min) |> Stream.minOrDefault 0.0 |> Converter.roundResult
              Mean = statsStream |> Stream.map(fun x -> % x.Mean) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              Max = statsStream |> Stream.map(fun x -> % x.Max) |> Stream.maxOrDefault 0.0 |> Converter.roundResult
              RPS = rps |> Converter.roundResult
              Percent50 = statsStream |> Stream.map(fun x -> % x.Percent50) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              Percent75 = statsStream |> Stream.map(fun x -> % x.Percent75) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              Percent95 = statsStream |> Stream.map(fun x -> % x.Percent95) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              Percent99 = statsStream |> Stream.map(fun x -> % x.Percent99) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              StdDev = statsStream |> Stream.map(fun x -> % x.StdDev) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              LatencyCount = { Less800 = less800; More800Less1200 = more800Less1200; More1200 = more1200 }
              MinDataKb = statsStream |> Stream.map(fun x -> % x.MinDataKb) |> Stream.minOrDefault 0.0 |> Converter.roundResult
              MeanDataKb = statsStream |> Stream.map(fun x -> % x.MeanDataKb) |> Stream.averageOrDefault 0.0 |> Converter.roundResult
              MaxDataKb = statsStream |> Stream.map(fun x -> % x.MaxDataKb) |> Stream.maxOrDefault 0.0 |> Converter.roundResult
              AllDataMB = statsStream |> Stream.sumBy(fun x -> % x.AllDataMB) |> Converter.roundResult
              ErrorStats = errorStats |> Stream.toArray })

module ScenarioStats =

    let create (scenario: Scenario) (simulationStats: LoadSimulationStats)
               (duration: TimeSpan) (stepsStats: Stream<StepStats>) =

        let createByStepStats (scnName: ScenarioName) (duration: TimeSpan)
                              (simulationStats: LoadSimulationStats)
                              (mergedStats: Stream<StepStats>) =

            let less800 = mergedStats |> Stream.sumBy(fun x -> x.LatencyCount.Less800)
            let more800Less1200 = mergedStats |> Stream.sumBy(fun x -> x.LatencyCount.More800Less1200)
            let more1200 = mergedStats |> Stream.sumBy(fun x -> x.LatencyCount.More1200)

            { ScenarioName = scnName
              RequestCount = mergedStats |> Stream.sumBy(fun x -> x.RequestCount)
              OkCount = mergedStats |> Stream.sumBy(fun x -> x.OkCount)
              FailCount = mergedStats |> Stream.sumBy(fun x -> x.FailCount)
              AllDataMB = mergedStats |> Stream.sumBy(fun x -> x.AllDataMB) |> Converter.roundResult
              StepStats = mergedStats |> Stream.toArray
              LatencyCount = { Less800 = less800; More800Less1200 = more800Less1200; More1200 = more1200 }
              LoadSimulationStats = simulationStats
              Duration = duration
              ErrorStats = mergedStats |> ErrorStats.merge |> Stream.toArray }

        stepsStats
        |> StepStats.merge
        |> createByStepStats scenario.ScenarioName duration simulationStats

module NodeStats =

    let create (testInfo: TestInfo) (nodeInfo: NodeInfo)
               (scnStats: Stream<ScenarioStats>)
               (pluginStats: Stream<DataSet>) =

        { RequestCount = scnStats |> Stream.sumBy(fun x -> x.RequestCount)
          OkCount = scnStats |> Stream.sumBy(fun x -> x.OkCount)
          FailCount = scnStats |> Stream.sumBy(fun x -> x.FailCount)
          AllDataMB = scnStats |> Stream.sumBy(fun x -> x.AllDataMB)
          ScenarioStats = scnStats |> Stream.toArray
          PluginStats = pluginStats |> Stream.toArray
          NodeInfo = nodeInfo
          TestInfo = testInfo
          ReportFiles = Array.empty }
