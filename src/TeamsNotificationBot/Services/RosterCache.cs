using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// The rosters person mentions are checked against, cached per conversation for
/// <see cref="Lifetime"/>, so a burst of messages into one channel reads its roster once, even when
/// they are processed at the same time. The flip side: someone just added to the team can take
/// that long to become mentionable. Only rosters used within the lifetime are kept: each read
/// drops the expired ones, so memory follows recent traffic, not every conversation ever seen.
/// </summary>
public sealed class RosterCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // The load is shared through the Lazy: concurrent callers that miss together await one read.
    private sealed record Entry(DateTimeOffset Expires, Lazy<Task<Roster>> Roster);

    private readonly ConcurrentDictionary<string, Entry> _rosters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public RosterCache(TimeProvider time) => _time = time;

    /// <summary>How many rosters are held. For the tests.</summary>
    internal int Count => _rosters.Count;

    /// <summary>The roster of <paramref name="conversationId"/>, read with <paramref name="load"/> when not cached.</summary>
    public async Task<Roster> GetAsync(string conversationId, Func<Task<IEnumerable<ChannelAccount>>> load)
    {
        var now = _time.GetUtcNow();
        if (!_rosters.TryGetValue(conversationId, out var entry) || entry.Expires <= now)
        {
            RemoveExpired(now);
            var fresh = new Entry(now + Lifetime, new Lazy<Task<Roster>>(async () => new Roster(await load())));
            entry = _rosters.AddOrUpdate(conversationId, fresh,
                (_, current) => current.Expires > now ? current : fresh);
        }

        try
        {
            return await entry.Roster.Value;
        }
        catch
        {
            // A failed read isn't cached: the next message reads again. Only this entry is removed,
            // not one a later caller has put in its place.
            _rosters.TryRemove(KeyValuePair.Create(conversationId, entry));
            throw;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (conversationId, entry) in _rosters)
        {
            if (entry.Expires <= now)
                _rosters.TryRemove(KeyValuePair.Create(conversationId, entry));
        }
    }
}

/// <summary>A conversation's members, found by Entra object ID or UPN (both case-insensitive).</summary>
public sealed class Roster
{
    /// <summary>
    /// A member's Entra object ID. Teams names it inconsistently by endpoint: the paged roster
    /// (teams and channels) sends <c>aadObjectId</c>, the whole-roster call (group chats) sends
    /// <c>objectId</c>, which the Agents SDK keeps in <c>Properties</c>.
    /// </summary>
    public static string? ObjectIdOf(ChannelAccount member) =>
        !string.IsNullOrEmpty(member.AadObjectId) ? member.AadObjectId
        : member.Properties.TryGetValue("objectId", out var objectId) && objectId.ValueKind == JsonValueKind.String
            ? objectId.GetString()
            : null;

    private readonly Dictionary<string, ChannelAccount> _byObjectId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChannelAccount> _byUpn = new(StringComparer.OrdinalIgnoreCase);

    public Roster(IEnumerable<ChannelAccount> members)
    {
        foreach (var member in members.Where(m => m != null))
        {
            if (ObjectIdOf(member) is { Length: > 0 } objectId)
                _byObjectId[NormalizeObjectId(objectId)] = member;
            // Teams sends the UPN as an extra property, which the Agents SDK keeps in Properties.
            if (member.Properties.TryGetValue("userPrincipalName", out var upn) &&
                upn.ValueKind == JsonValueKind.String &&
                upn.GetString() is { Length: > 0 } value)
            {
                _byUpn[value] = member;
            }
        }
    }

    public static readonly Roster Empty = new([]);

    /// <summary>The member with Entra object ID or UPN <paramref name="id"/>, or null.</summary>
    public ChannelAccount? Find(string id) => Guid.TryParse(id, out _)
        ? _byObjectId.GetValueOrDefault(NormalizeObjectId(id))
        : _byUpn.GetValueOrDefault(id);

    private static string NormalizeObjectId(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("D") : id;
}
