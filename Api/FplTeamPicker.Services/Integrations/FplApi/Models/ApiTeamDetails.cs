using Team = FplTeamPicker.Domain.Models.Team;

namespace FplTeamPicker.Services.Integrations.FplApi.Models;

public record ApiTeamDetails
{
    public int Code { get; set; }

    public required string Name { get; set; }

    public required string ShortName { get; set; }

    public int Id { get; set; }

    public Team ToTeam()
    {
        return new Team
        {
            Code = Code,
            Name = Name,
            ShortName = ShortName,
            Id = Id
        };
    }
}