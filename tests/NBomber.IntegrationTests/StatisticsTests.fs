module Tests.Statistics

open System
open System.Threading.Tasks

open Xunit
open FsCheck.Xunit
open Swensen.Unquote
open Nessos.Streams
open FSharp.Control.Tasks.NonAffine

open NBomber.Contracts
open NBomber.Domain
open NBomber.Domain.DomainTypes
open NBomber.FSharp
open NBomber.Extensions.InternalExtensions
open Tests.TestHelper

let private latencyCount = { Less800 = 1; More800Less1200 = 1; More1200 = 1 }

let private scenario = {
    ScenarioName = "Scenario1"
    Init = None
    Clean = None
    Steps = List.empty
    LoadSimulations = [KeepConstant(copies = 1, during = seconds 1)]
    WarmUpDuration = TimeSpan.FromSeconds(1.0)
    GetStepsOrder = fun () -> Array.empty
}

[<Property>]
let ``calcMin() should not fail`` (requestCounts: int list) =
    requestCounts
    |> Seq.map(fun x -> Statistics.calcRPS x (TimeSpan.FromMinutes 1.0))
    |> ignore

[<Fact>]
let ``ErrorStats should be calculated properly`` () =

    let okStep = Step.createAsync("ok step", fun _ -> task {
        do! Task.Delay(milliseconds 100)
        return Response.ok()
    })

    let failStep1 = Step.createAsync("fail step 1", fun context -> task {
        do! Task.Delay(milliseconds 10)
        return if context.InvocationCount <= 10 then Response.fail(reason = "reason 1", errorCode = 10)
               else Response.ok()
    })

    let failStep2 = Step.createAsync("fail step 2", fun context -> task {
        do! Task.Delay(milliseconds 10)
        return if context.InvocationCount <= 30 then Response.fail(reason = "reason 2", errorCode = 20)
               else Response.ok()
    })

    let scenario =
        Scenario.create "realtime stats scenario" [okStep; failStep1; failStep2]
        |> Scenario.withoutWarmUp
        |> Scenario.withLoadSimulations [
            KeepConstant(copies = 2, during = seconds 10)
        ]

    NBomberRunner.registerScenarios [scenario]
    |> NBomberRunner.withReportFolder "./stats-tests/1/"
    |> NBomberRunner.run
    |> Result.getOk
    |> fun stats ->
        let scnErrorsStats = stats.ScenarioStats.[0].ErrorStats
        let fail1Stats = stats.ScenarioStats.[0].StepStats.[1].ErrorStats
        let fail2Stats = stats.ScenarioStats.[0].StepStats.[2].ErrorStats

        test <@ scnErrorsStats.Length = 2 @>
        test <@ fail1Stats.Length = 1 @>
        test <@ fail2Stats.Length = 1 @>

        test <@ fail1Stats
                |> Seq.find(fun x -> x.ErrorCode = 10)
                |> fun error -> error.Count = 20 @>

        test <@ fail2Stats
                |> Seq.find(fun x -> x.ErrorCode = 20)
                |> fun error -> error.Count = 60 @>
