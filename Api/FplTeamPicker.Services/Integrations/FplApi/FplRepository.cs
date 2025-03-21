using System.Net.Http.Json;
using System.Text.Json;
using FplTeamPicker.Domain.Contracts;
using FplTeamPicker.Domain.Models;
using FplTeamPicker.Services.Caching.Constants;
using FplTeamPicker.Services.Integrations.FplApi.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FplTeamPicker.Services.Integrations.FplApi;

public class FplRepository(
    HttpClient httpClient,
    JsonSerializerOptions serializerOptions,
    IFplUserProvider fplUserProvider,
    ILogger<FplRepository> logger,
    IMemoryCache memoryCache) : IFplRepository, IDisposable
{
    public async Task<User> GetUserDetailsAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "api/me");
        var result = await MakeRequestAsync<ApiUserDetails>(request, cancellationToken);

        memoryCache.Set(CacheKeys.UserLookup(fplUserProvider.GetUserId()), result.User.Entry);

        return new User
        {
            FirstName = result.User.FirstName,
            LastName = result.User.LastName,
            Id = result.User.Entry
        };
    }

    public async Task<SelectedTeam> GetSelectedTeamAsync(CancellationToken cancellationToken)
    {
        var userId = await GetManagerIdAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/my-team/{userId}");
        var result = await MakeRequestAsync<ApiTeam>(request, cancellationToken);
        var team = new SelectedTeam
        {
            Bank = result.Transfers.Bank,
            FreeTransfers = result.Transfers.Limit - result.Transfers.Made
        };

        foreach (var pick in result.Picks)
        {
            var playerDetails = await LookupPlayerAsync(pick.Id, cancellationToken);
            var selectedPlayer = new SelectedPlayer
            {
                IsCaptain = pick.IsCaptain,
                IsViceCaptain = pick.IsViceCaptain,
                Player = playerDetails,
                SellingPrice = pick.SellingPrice
            };

            if (pick.SquadNumber <= 11)
            {
                team.StartingXi.Add(selectedPlayer);
            }
            else
            {
                team.Bench.Add(selectedPlayer);
            }
        }

        return team;
    }

    public async Task<List<League>> GetLeaguesAsync(CancellationToken cancellationToken)
    {
        var userId = await GetManagerIdAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/entry/{userId}");
        var result = await MakeRequestAsync<ApiEntry>(request, cancellationToken);
        var leagues = new List<League>();
        foreach (var classicLeague in result.Leagues.Classic.Where(c => c.LeagueType == "x"))
        {
            var leagueRequest =
                new HttpRequestMessage(HttpMethod.Get, $"api/leagues-classic/{classicLeague.Id}/standings");
            var leagueResult = await MakeRequestAsync<ApiLeagueDetails>(leagueRequest, cancellationToken);
            if (leagueResult.Standings.HasNext)
            {
                throw new Exception("Lots of players!");
            }

            var league = new League
            {
                Id = classicLeague.Id,
                Name = classicLeague.Name,
                CurrentPosition = classicLeague.EntryRank,
                Participants = leagueResult.Standings.Results
                    .Select(r => new LeagueParticipant
                    {
                        UserId = r.Entry,
                        PlayerNam = r.PlayerName,
                        TeamName = r.TeamName,
                        Position = r.Rank
                    })
                    .ToList()
            };
            leagues.Add(league);
        }

        return leagues;
    }

    public async Task<List<Player>> GetPlayersAsync(CancellationToken cancellationToken)
    {
        var players = new List<Player>();
        var request = new HttpRequestMessage(HttpMethod.Get, "api/bootstrap-static");
        var result = await MakeRequestAsync<ApiDataDump>(request, cancellationToken);
        foreach (var player in result.Players
                     .Where(p => p.Position != ApiPosition.Manager)
                     .Select(p => p.ToPlayer())
                     .OrderBy(p => p.Id))
        {
            memoryCache.Set(CacheKeys.PlayerLookup(player.Id), player);
            players.Add(player);
        }

        return players;
    }

    public async Task<List<Team>> GetTeamsAsync(CancellationToken cancellationToken)
    {
        var teams = new List<Team>();
        var request = new HttpRequestMessage(HttpMethod.Get, "api/bootstrap-static");
        var result = await MakeRequestAsync<ApiDataDump>(request, cancellationToken);

        foreach (var team in result.Teams
                     .Select(p => p.ToTeam())
                     .OrderBy(p => p.ShortName))
        {
            memoryCache.Set(CacheKeys.PlayerLookup(team.Code), team);
            teams.Add(team);
        }

        return teams;
    }

    private async Task<int> GetManagerIdAsync(CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.UserLookup(fplUserProvider.GetUserId());
        if (!memoryCache.TryGetValue(cacheKey, out int managerId))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "api/me");
            var result = await MakeRequestAsync<ApiUserDetails>(request, cancellationToken);
            managerId = result.User.Entry;
            memoryCache.Set(cacheKey, managerId);
        }

        return managerId;
    }

    private async Task<Player> LookupPlayerAsync(int playerId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.PlayerLookup(playerId);
        if (!memoryCache.TryGetValue(cacheKey, out Player? playerDetails))
        {
            await GetPlayersAsync(cancellationToken);
        }

        return memoryCache.TryGetValue(cacheKey, out playerDetails)
            ? playerDetails!
            : throw new Exception("Player not found");
    }

    private async Task<TApiModel> MakeRequestAsync<TApiModel>(HttpRequestMessage request,
        CancellationToken cancellationToken)
        where TApiModel : class
    {
        var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("API Error: {StatusCode} {Message}", response.StatusCode, errorMessage);
            throw new Exception("API Request failed");
        }

        var result = await response.Content.ReadFromJsonAsync<TApiModel>(serializerOptions, cancellationToken);
        return result!;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}