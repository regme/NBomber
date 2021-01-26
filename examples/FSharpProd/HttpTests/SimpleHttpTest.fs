module FSharpProd.HttpTests.SimpleHttpTest

open System.Net.Http
open NBomber
open NBomber.Contracts
open NBomber.FSharp
open NBomber.Plugins.Http.FSharp
open NBomber.Plugins.Network.Ping
open FSharp.Control.Tasks.V2.ContextInsensitive

// in this example we use:
// - NBomber.Http (https://nbomber.com/docs/plugins-http)

let run () =

//    let step = HttpStep.create("fetch_html_page", fun context ->
//        Http.createRequest "GET" "https://nbomber.com"
//        |> Http.withHeader "Accept" "text/html"
//    )

    use httpClient = new HttpClient()

    let step = Step.create("fetch_html_page", fun context -> task {
        let! response = httpClient.GetAsync("https://nbomber.com")
        return if response.IsSuccessStatusCode then Response.Ok()
               else Response.Fail()
    })

    let pingPluginConfig = PingPluginConfig.CreateDefault ["nbomber.com"]
    let pingPlugin = new PingPlugin(pingPluginConfig)

    Scenario.create "nbomber_web_site" [step]
    |> Scenario.withWarmUpDuration(seconds 5)
    |> Scenario.withLoadSimulations [InjectPerSec(rate = 500, during = seconds 30)]
    |> NBomberRunner.registerScenario
    |> NBomberRunner.withWorkerPlugins [pingPlugin]
    |> NBomberRunner.withTestSuite "http"
    |> NBomberRunner.withTestName "simple_test"
    |> NBomberRunner.run
    |> ignore
