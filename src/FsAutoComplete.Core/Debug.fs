﻿// --------------------------------------------------------------------------------------
// (c) Robin Neatherway
// --------------------------------------------------------------------------------------
namespace FsAutoComplete

/// loggers that are shared between components
[<RequireQualifiedAccess>]
module Loggers =
  open FsAutoComplete.Logging

  let analyzers = LogProvider.getLoggerByName "Analyzers"


[<RequireQualifiedAccess>]
module Debug =
  open System
  open System.Collections.Concurrent
  open FsAutoComplete.Logging

  let toggleVerboseLogging (verbose: bool) = () // todo: set logging latch

  let waitForDebugger () =
    while not (Diagnostics.Debugger.IsAttached) do
      System.Threading.Thread.Sleep(100)

  type LogCompilerFunctionId =
    | Service_ParseAndCheckFileInProject = 1
    | Service_CheckOneFile = 2
    | Service_IncrementalBuildersCache_BuildingNewCache = 3
    | Service_IncrementalBuildersCache_GettingCache = 4
    | CompileOps_TypeCheckOneInputAndFinishEventually = 5
    | IncrementalBuild_CreateItemKeyStoreAndSemanticClassification = 6
    | IncrementalBuild_TypeCheck = 7

  let logFunctionName (payload: obj) =
    Log.addContextDestructured "function" (payload :?> int |> enum<LogCompilerFunctionId>)


  module FSharpCompilerEventLogger =
    open System.Diagnostics.Tracing

    let logger = LogProvider.getLoggerByName "Compiler"

    /// listener for the the events generated by the `FSharp.Compiler.FSharpCompilerEventSource`
    type Listener() =
      inherit EventListener()

      let mutable source = null

      let inflightEvents = ConcurrentDictionary<Guid, DateTimeOffset>()

      let eventLevelToLogLevel (e: EventLevel) =
        match e with
        | EventLevel.Critical -> logger.fatal
        | EventLevel.Error -> logger.error
        | EventLevel.Informational -> logger.info
        | EventLevel.LogAlways -> logger.fatal
        | EventLevel.Verbose -> logger.debug
        | EventLevel.Warning -> logger.warn
        | _ -> logger.info

      override __.OnEventSourceCreated newSource =
        if newSource.Name = "FSharpCompiler" then
          base.EnableEvents(newSource, EventLevel.LogAlways, EventKeywords.All)
          source <- newSource
      override __.OnEventWritten eventArgs =

        let message =
          match eventArgs.EventName with
          | "Log" -> Log.setMessage "Inside Compiler Function {function}" >> logFunctionName eventArgs.Payload.[0]
          | "LogMessage" -> Log.setMessage "({function}) {message}" >> logFunctionName eventArgs.Payload.[1] >> Log.addContextDestructured "message" (eventArgs.Payload.[0] :?> string)
          | "BlockStart" | "BlockMessageStart" ->
             inflightEvents.TryAdd(eventArgs.RelatedActivityId, DateTimeOffset.UtcNow) |> ignore
             id
          | "BlockEnd" ->
            match inflightEvents.TryRemove(eventArgs.RelatedActivityId) with
            | true, startTime ->
              let delta = DateTimeOffset.UtcNow - startTime
              Log.setMessage "Finished compiler function {function} in {seconds}" >> logFunctionName eventArgs.Payload.[0] >> Log.addContextDestructured "seconds" delta.TotalSeconds
            | false, _ -> id
          | "BlockMessageStop" ->
            match inflightEvents.TryRemove(eventArgs.RelatedActivityId) with
            | true, startTime ->
              let delta = DateTimeOffset.UtcNow - startTime
              Log.setMessage "Finished compiler function {function} with parameter {parameter} in {seconds}"
              >> logFunctionName eventArgs.Payload.[1]
              >> Log.addContextDestructured "seconds" delta.TotalSeconds
              >> Log.addContextDestructured "parameter" (eventArgs.Payload.[0])
            | false, _ -> id
          | other ->
            Log.setMessage "Unknown event {name} with payload {payload}" >> Log.addContextDestructured "name" eventArgs.EventName >> Log.addContextDestructured "payload" (eventArgs.Payload |> Seq.toList)

        (eventLevelToLogLevel eventArgs.Level) message

      interface System.IDisposable with
        member __.Dispose () =
          if isNull source then () else base.DisableEvents(source)
