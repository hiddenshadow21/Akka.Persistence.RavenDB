﻿using Akka.Actor;
using Akka.Persistence.Query;
using Raven.Client.Documents.Changes;
using System.Threading.Channels;

namespace Akka.Persistence.RavenDb.Query.ContinuousQuery;

public class EventsByPersistenceId : ContinuousQuery<DocumentChange>
{
    private readonly string _persistenceId;
    private long _fromSequenceNr;
    private readonly long _toSequenceNr;

    public EventsByPersistenceId(string persistenceId, long fromSequenceNr, long toSequenceNr, Channel<EventEnvelope> channel, RavenDbReadJournal ravendb) : base(ravendb, channel)
    {
        _persistenceId = persistenceId;
        _fromSequenceNr = fromSequenceNr - 1;
        _toSequenceNr = toSequenceNr;
    }

    protected override IChangesObservable<DocumentChange> Subscribe(IDatabaseChanges changes)
    {
        var prefix = Ravendb.Store.GetEventPrefix(_persistenceId);
        return changes.ForDocumentsStartingWith(prefix);
    }

    protected override async Task QueryAsync()
    {
        using var session = Ravendb.Store.Instance.OpenAsyncSession();
        session.Advanced.SessionInfo.SetContext(_persistenceId);

        using var cts = Ravendb.Store.GetReadCancellationTokenSource();
        await using var results = await session.Advanced.StreamAsync<Journal.Types.Event>(startsWith: Ravendb.Store.GetEventPrefix(_persistenceId),
            startAfter: Ravendb.Store.GetEventSequenceId(_persistenceId, _fromSequenceNr), token: cts.Token).ConfigureAwait(false);
        while (await results.MoveNextAsync().ConfigureAwait(false))
        {
            var @event = results.Current.Document;
            if (results.Current.Document.SequenceNr > _toSequenceNr)
                break;

            var persistent = Journal.Types.Event.Deserialize(Ravendb.Storage.Serialization, @event, ActorRefs.NoSender);
            var e = new EventEnvelope(new Sequence(@event.Timestamp.Ticks), @event.PersistenceId, @event.SequenceNr, persistent.Payload, @event.Timestamp.Ticks,
                @event.Tags);
            await Channel.Writer.WriteAsync(e, cts.Token).ConfigureAwait(false);
            _fromSequenceNr = e.SequenceNr;
        }

        
        if (_fromSequenceNr >= _toSequenceNr)
        {
            Channel.Writer.TryComplete();
        }
    }
}