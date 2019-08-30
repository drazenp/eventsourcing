module EventStore =

    type EventProducer<'Event> =
        'Event list -> 'Event list

    type EventStore<'Event> =
        {
            Get: unit -> 'Event list
            Append: 'Event list -> unit
            Evolve: EventProducer<'Event> -> unit
        }

    type Msg<'Event> =
        | Append of 'Event list
        | Get of AsyncReplyChannel<'Event list> // what kind of reply do we expect
        | Evolve of EventProducer<'Event>

    let initialize () : EventStore<'Event> =

        let agent =
            MailboxProcessor.Start(fun inbox ->
                // start can be any state the agent should store
                let rec loop history =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | Append events ->
                            // call the recursive function to let the agent live
                            return! loop (history @ events)

                        | Get reply ->
                            // reply on the given channel
                            reply.Reply history

                            // call the recursion function to let the agent live
                            return! loop history

                        | Evolve eventProducer ->
                            let newEvents =
                                eventProducer history

                            return! loop (history @ newEvents)
                    }

                loop []
            )


        let append events =
            agent.Post (Append events)   

        let get () =
            agent.PostAndReply Get

        let evolve eventProducer =
            agent.Post (Evolve eventProducer)

        {
            Get = get
            Append = append
            Evolve = evolve
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

    let printEvents events =
        events
        |> List.length
        |> printfn "History (Length: %i)"

        events |> printUl

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

    eventStore.Evolve (Behaviour.cellFlavour Vanilla)
    eventStore.Evolve (Behaviour.cellFlavour Strawberry)

    eventStore.Evolve (Behaviour.restock Vanilla 3)

    eventStore.Evolve (Behaviour.cellFlavour Vanilla)

    // eventStore.Append [Flavour_restocked (Vanilla, 3)]
    // eventStore.Append [Flavour_sold Vanilla]
    // eventStore.Append [Flavour_sold Vanilla]
    // eventStore.Append [Flavour_sold Vanilla; Flavour_went_out_of_stock Vanilla]

    let events = eventStore.Get()


    events |> printEvents

    let sold : Map<Flavour, int> =
        events
        |> project soldFlavours


    printSoldFlavour Vanilla sold
    printSoldFlavour Strawberry sold

    let stock =
        events |> project flavoursInStock

    0