﻿module AsyncTests

open FSharp.Control
open Kafunk
open NUnit.Framework
open System.Threading
open System.Threading.Tasks

module Async =
  
  let runTimeout (timeoutMs:int) (a:Async<'a>) : 'a =
    Async.RunSynchronously (a, timeoutMs)

  let runTest a = runTimeout 5000 a



[<Test>]
let ``Async.never should regard infinite timeouts as equal`` () =
  Assert.AreEqual (Async.never, Async.never)

[<Test>]
let ``Async.choose should choose first to complete`` () =
  for fastMs in [20..30] do
    let fast = Async.Sleep fastMs |> Async.map Choice1Of2
    let slow = Async.Sleep (fastMs + 20) |> Async.map Choice2Of2
    let first = Async.choose fast slow
    Assert.AreEqual (fast, first)

[<Test>]
let ``MVar.updateAsync should execute update serially`` () =
  let mv = MVar.create ()
  
  let calling = 1
  let calls = 100

  mv |> MVar.put calling |> Async.RunSynchronously |> ignore
  
  let st = ref 0

  let update i = async {
    do! Async.SwitchToThreadPool ()
    return i + 1 }

  let op calling = async {
    let! i' =
      mv 
      |> MVar.updateAsync (fun i -> async {
        if Interlocked.CompareExchange (st, 1, 0) <> 0 then
          return failwith "overlapping execution detected"
        let! r = 
          if i = calling then update i
          else async { return i }
        if Interlocked.CompareExchange (st, 0, 1) <> 1 then
          return failwith "overlapping execution detected"
        return r })
    return i' }

  let actual = 
    Async.Parallel (Seq.init calls (fun _ -> op calling))
    //|> Async.RunSynchronously
    |> Async.runTest
    |> List.ofArray
  
  let expected = List.init calls (fun _ -> calling + 1)
  
  shouldEqual expected actual None


[<Test>]
let ``Async.withCancellation should cancel`` () =
  
  let cts = new CancellationTokenSource()
  let cancelled = ref false

  let comp = async {
    let! ct = Async.CancellationToken
    ct.Register (fun () -> cancelled := true) |> ignore
    while true do
      do! Async.Sleep 2 }

  let cancellableComp = Async.cancelWithToken cts.Token comp

  cts.CancelAfter 200

  let expected = None
  let actual = Async.runTimeout 5000 cancellableComp

  shouldEqual expected actual None
  shouldEqual true !cancelled None


[<Test>]
let ``Async.choose should respect ambient cancellation token`` () =
  
  let cancelled0 = ref false
  let cancelled1 = ref false
  let cancelled2 = ref false

  let comp1 = async {
    let! ct = Async.CancellationToken
    ct.Register (fun () -> cancelled1 := true) |> ignore
    while not (ct.IsCancellationRequested) do
      () }

  let comp2 = async {
    let! ct = Async.CancellationToken
    ct.Register (fun () -> cancelled2 := true) |> ignore
    while not (ct.IsCancellationRequested) do
      () }

  let r = TaskCompletionSource<unit>()

  let c = async {
    let! ct = Async.CancellationToken
    ct.Register (fun () -> cancelled0 := true) |> ignore
    let! _ = Async.choose comp1 comp2
    r.SetResult() }

  let cts = new CancellationTokenSource()

  Async.Start (c, cts.Token)

  cts.CancelAfter 50

  let completed = r.Task.Wait (200)

  shouldEqual true !cancelled0 None
  shouldEqual true !cancelled1 None
  shouldEqual true !cancelled2 None

[<Test>]
let ``AsyncSeq.windowed should work`` () =
  for windowSize in [1..5] do
    for inputSize in [0..10] do
      let input = List.init inputSize id
      let expected = 
        input
        |> Seq.windowed windowSize
        |> Seq.map List.ofArray 
        |> Seq.toList
      let actual = 
        AsyncSeq.ofSeq input
        |> AsyncSeq.windowed windowSize 
        |> AsyncSeq.map List.ofArray
        |> AsyncSeq.toListAsync
        |> Async.runTest
      shouldEqual expected actual None

[<Test>]
let ``AsyncSeq.iterAsyncParallel should propagate exception`` () =
  
  for N in [100] do
    
    let fail = N / 2

    let res = 
      Seq.init N id
      |> AsyncSeq.ofSeq
      |> FSharp.Control.AsyncSeq.mapAsyncParallel (fun i -> async {
        if i = fail then 
          return failwith  "error"
        return i })
//      |> AsyncSeq.iterAsyncParallel (fun i -> async {
//        if i = fail then 
//          return failwith "error"
//        else () })
      //|> AsyncSeq.iter ignore
      |> AsyncSeq.iterAsyncParallel (async.Return >> Async.Ignore)
      |> Async.Catch
      |> Async.runTest
  
    match res with
    | Failure _ -> ()
    | Success _ -> Assert.Fail ("error expected")

[<Test>]
let ``AsyncSeq.iterAsyncParallelThrottled should propagate handler exception`` () =
  
  let res =
    AsyncSeq.init 100L id
    |> AsyncSeq.iterAsyncParallelThrottled 10 (fun i -> async { if i = 50L then return failwith "oh no" else return () })
    |> Async.Catch
    |> (fun x -> Async.RunSynchronously (x, timeout = 10000))

  match res with
  | Failure _ -> ()
  | Success _ -> Assert.Fail ("error expected") 

[<Test>]
let ``AsyncSeq.iterAsyncParallelThrottled should propagate sequence exception`` () =
  
  let res =
    asyncSeq {
      yield 1
      yield 2
      yield 3
      failwith "oh no"
    }
    |> AsyncSeq.iterAsyncParallelThrottled 10 (async.Return >> Async.Ignore)
    |> Async.Catch
    |> (fun x -> Async.RunSynchronously (x, timeout = 10000))

  match res with
  | Failure _ -> ()
  | Success _ -> Assert.Fail ("error expected")    


[<Test>]
let ``AsyncSeq.iterAsyncParallelThrottled should throttle`` () =
  
  let count = ref 0
  let parallelism = 10

  let res =
    AsyncSeq.init 100L id
    |> AsyncSeq.iterAsyncParallelThrottled parallelism (fun i -> async {
      let c = Interlocked.Increment count
      if c > parallelism then
        return failwith "oh no"
      do! Async.Sleep 10
      Interlocked.Decrement count |> ignore
      return () })
    |> Async.RunSynchronously

  ()
