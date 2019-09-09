module EventStore =

    type EventProducer<'Event> =
        'Event list -> 'Event list

    type Aggregate = System.Guid

    type EventStore<'Event> =
        {
            Get: unit -> Map<Aggregate, 'Event list>
            GetStream: Aggregate -> 'Event list
            Append: Aggregate -> 'Event list -> unit
            Evolve: Aggregate -> EventProducer<'Event> -> unit
        }

    type Msg<'Event> =
        | Append of Aggregate * 'Event list
        | GetStream of Aggregate * AsyncReplyChannel<'Event list>
        | Get of AsyncReplyChannel<Map<Aggregate, 'Event list>> // what kind of reply do we expect
        | Evolve of Aggregate * EventProducer<'Event>


    let eventsForAggregate aggregate history =
        history
        |> Map.tryFind aggregate
        |> Option.defaultValue []

    let initialize () : EventStore<'Event> =

        let agent =
            MailboxProcessor.Start(fun inbox ->
                // start can be any state the agent should store
                let rec loop history =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | Append (aggregate, events) ->
                            let stream_events =
                                history |> eventsForAggregate aggregate

                            let newHistory =
                                history
                                |> Map.add aggregate (stream_events @ events)

                            // call the recursive function to let the agent live
                            return! loop newHistory

                        | Get reply ->
                            // reply on the given channel
                            reply.Reply history

                            // call the recursion function to let the agent live
                            return! loop history

                        | GetStream (aggregate, reply) ->
                            reply.Reply (history |> eventsForAggregate aggregate)

                            return! loop history

                        | Evolve (aggregate, eventProducer) ->
                            let stream_events =
                                history |> eventsForAggregate aggregate

                            let newEvents =
                                eventProducer stream_events

                            let newHistory =
                                history
                                |> Map.add aggregate (stream_events @ newEvents)

                            return! loop newHistory
                    }

                loop Map.empty
            )


        let append aggregate events =
            agent.Post (Append (aggregate, events))   

        let get () =
            agent.PostAndReply Get

        let evolve aggregate eventProducer =
            agent.Post (Evolve (aggregate, eventProducer))

        let getStream aggregate =
            agent.PostAndReply (fun reply -> GetStream (aggregate, reply))

        {
            Get = get
            Append = append
            Evolve = evolve
            GetStream = getStream
        }

module Domain =

    type Flavour =
        | Strawberry
        | Vanilla

    type Event =
        | Flavour_sold of Flavour
        | Flavour_restocked of Flavour * int
        | Flavour_went_out_of_stock of Flavour
        | Flavour_was_not_in_stock of Flavour

module Projections =
    open Domain

    type Projection<'State, 'Event> =
        {
            Init: 'State
            Update: 'State -> 'Event -> 'State
        }

    let project (projection : Projection<_, _>) events =
        events |> List.fold projection.Update projection.Init

    let soldOfFlavour flavour state =
        state
        |> Map.tryFind flavour
        |> Option.defaultValue 0

    let updateSoldFlavouts state event =
        match event with
        | Flavour_sold flavour ->
            state
            |> soldOfFlavour flavour 
            |> fun portions -> state |> Map.add flavour (portions + 1)

        | _ -> state

    let soldFlavours : Projection<Map<Flavour, int>, Event> =
        {
            Init = Map.empty
            Update = updateSoldFlavouts
        }

    let restock flavour number stock =
        stock
        |> Map.tryFind flavour
        |> Option.defaultValue 0
        |> fun portion -> stock |> Map.add flavour (portion + number)

    let updateFlavoursInStock stock event =
        match event with
        | Flavour_sold flavour ->
            stock |> restock flavour -1

        | Flavour_restocked (flavour, number) ->
            stock |> restock flavour number

        | _ -> stock

    let flavoursInStock : Projection<Map<Flavour, int>, Event> =
        {
            Init = Map.empty
            Update = updateFlavoursInStock
        }

    let stockOf flavour stock =
        stock
        |> Map.tryFind flavour
        |> Option.defaultValue 0

module Behaviour =

    open Domain
    open Projections

    let cellFlavour flavour (events: Event list)  =

        // get stock for a specific flavour
        let stock =
            events
            |> project flavoursInStock
            |> stockOf flavour

        // check constraints for Flavour_sold
        match stock with
        | 0 -> [Flavour_was_not_in_stock flavour]
        | 1 -> [Flavour_sold flavour; Flavour_went_out_of_stock flavour]
        | _ -> [Flavour_sold flavour]

    let restock flavour number events =    
        [Flavour_restocked (flavour, number)]

module Tests =
    open Expecto
    open Expecto.Expect
    open Domain

    // Given events
    // When action
    // Then events

    let Given = id

    let When eventProducer events =
        eventProducer events

    let Then expectedEvenst acutalEvents =
        equal acutalEvents expectedEvenst "new events should equal expected events"

    let tests =
       testList "SellFlavour"
            [
                test "Flavour_sold happy path" {
                    Given [ Flavour_restocked (Vanilla, 3) ]
                    |> When (Behaviour.cellFlavour Vanilla)
                    |> Then [ Flavour_sold Vanilla ]
                }

                test "Flavour_sold, Flavour_went_out_of_stock" {
                    Given
                        [
                            Flavour_restocked (Vanilla, 3)
                            Flavour_sold Vanilla
                            Flavour_sold Vanilla
                        ]
                    |> When (Behaviour.cellFlavour Vanilla)
                    |> Then
                        [
                            Flavour_sold Vanilla
                            Flavour_went_out_of_stock Vanilla
                        ]
                }
                
                test "Flavour_was_not_in_stock" {
                    Given
                        [
                            Flavour_restocked (Vanilla, 3)
                            Flavour_sold Vanilla
                            Flavour_sold Vanilla
                            Flavour_sold Vanilla
                        ]
                    |> When (Behaviour.cellFlavour Vanilla)
                    |> Then
                        [
                            Flavour_was_not_in_stock Vanilla
                        ]
                }
            ]

module Helper =
    open Projections
    open Expecto
    
    let printUl list =
        list
        |> List.iteri (fun i item -> printfn " %i: %A" (i+1) item)

    let printEvents header events =
        events
        |> List.length
        |> printfn "History for %s (Length: %i)" header

        events |> printUl

    let printTotalHistory history =
        history
        |> Map.fold (fun length _ events -> length + (events |> List.length)) 0
        |> printfn "Total History Length: %i"

    let printSoldFlavour flavour state =
        state
        |> soldOfFlavour flavour
        |> printfn "Sold %A %i" flavour

    let runTests () =
        runTests defaultConfig Tests.tests |> ignore

open EventStore
open Domain
open Helper
open Projections

[<EntryPoint>]
let main _ =

    runTests ()

    let eventStore : EventStore<Event> = EventStore.initialize()

    let truck1 = System.Guid.NewGuid()
    let truck2 = System.Guid.NewGuid()

    eventStore.Evolve truck1 (Behaviour.cellFlavour Vanilla)
    eventStore.Evolve truck1 (Behaviour.cellFlavour Strawberry)
    eventStore.Evolve truck1 (Behaviour.restock Vanilla 3)
    eventStore.Evolve truck1 (Behaviour.cellFlavour Vanilla)

    eventStore.Evolve truck2 (Behaviour.cellFlavour Vanilla)

    let eventTruck1 = eventStore.GetStream truck1
    let eventTruck2 = eventStore.GetStream truck2

    // let events = eventStore.Get()


    eventTruck1 |> printEvents "Truck 1"
    eventTruck2 |> printEvents "Truck 2"

    let sold : Map<Flavour, int> =
        eventTruck1 |> project soldFlavours


    printSoldFlavour Vanilla sold
    printSoldFlavour Strawberry sold

    let stock =
        eventTruck1 |> project flavoursInStock

    0