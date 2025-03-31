using FplTeamPicker.Domain.Models;
using FplTeamPicker.Services.Optimisation.Exceptions;
using FplTeamPicker.Services.Optimisation.Models;
using Google.OrTools.Sat;
using LinearExpr = Google.OrTools.Sat.LinearExpr;

namespace FplTeamPicker.Services.Optimisation;

public class FplTeamTransfersSolver
{
    private readonly FplTeamTransfersRequest _request;

    public FplTeamTransfersSolver(FplTeamTransfersRequest request)
    {
        _request = request;
    }

    public FplTransfersSelection Solve()
    {
        var model = new FplTeamTransfersCpModel();

        InitialiseVars(model);

        AddConstraints(model);

        AddObjective(model);

        return Solve(model);
    }

    private FplTransfersSelection Solve(FplTeamTransfersCpModel model)
    {
        var cpSolver = new CpSolver();
        var status = cpSolver.Solve(model);
        if (status != CpSolverStatus.Optimal)
        {
            throw OptimisationException.SubOptimalCpSolution(status);
        }

        var output = new FplTransfersSelection();
        foreach (var playerSelectionVar in model.Selections)
        {
            if (!cpSolver.BooleanValue((BoolVar)playerSelectionVar.SquadSelected))
            {
                continue;
            }
            var player = _request.AllPlayers.Single(p => p.Id == playerSelectionVar.Id);
            var selectedPlayer = new SelectedPlayer
            {
                Player = player,
                SellingPrice = player.Cost
            };

            var teamSelected = cpSolver.BooleanValue((BoolVar)playerSelectionVar.TeamSelected);
            if (teamSelected)
            {
                output.StartingXi.Add(selectedPlayer);
            }
            else
            {
                output.Bench.Add(selectedPlayer);
            }
        }

        return output;
    }

    private void InitialiseVars(FplTeamTransfersCpModel model)
    {
        foreach (var existingPlayer in _request.ExistingPlayers)
        {
            var squadSelection = GetSelectionVar(model, existingPlayer, isExisting: true);

            InitTeamSelectionCount(model, existingPlayer, squadSelection.SquadSelected);
        }

        foreach (var otherPlayer in _request.OtherPlayers)
        {
            var squadSelection = GetSelectionVar(model, otherPlayer, isExisting: false);

            InitTeamSelectionCount(model, otherPlayer, squadSelection.SquadSelected);
        }
    }

    private static void InitTeamSelectionCount(FplTeamTransfersCpModel model, Player existingPlayer,
        IntVar squadSelected)
    {
        if (model.TeamSelectionCounts.ContainsKey(existingPlayer.Team))
        {
            model.TeamSelectionCounts[existingPlayer.Team].Add(squadSelected);
        }
        else
        {
            model.TeamSelectionCounts.Add(existingPlayer.Team, new List<IntVar> { squadSelected});
        }
    }

    private void AddObjective(FplTeamTransfersCpModel model)
    {
        // Get the scale product of the selection booleans and multiply by each player's XI selection, maximizing the total.
        const int multiplier = 100;
        var allPlayerPredictedPoints = _request.AllPlayers.Select(p => (int)Math.Round(p.XpNext * multiplier)).ToList();
        var predictedPointsForTeam = LinearExpr.WeightedSum(model.Selections.Select(p => p.TeamSelected), allPlayerPredictedPoints);

        // (# transfers - free transfers) * op
        var transferSum = LinearExpr.Sum(model.TransferSelections.Select(p => p.SquadSelected));

        var penaliseTransfers = model.NewBoolVar("penaliseTransfers");
        model.Add(transferSum > _request.NumberTransfers).OnlyEnforceIf(penaliseTransfers);
        model.Add(transferSum <= _request.NumberTransfers).OnlyEnforceIf(penaliseTransfers.Not());
        var penalisedTransferCount = model.NewIntVar(0, _request.Options.SquadCount, "penalisedTransferCount");
        model.Add(penalisedTransferCount == transferSum - _request.NumberTransfers).OnlyEnforceIf(penaliseTransfers);
        var transferPenalty = penalisedTransferCount * _request.Options.TransferPointsPenalty * multiplier;
        
        var allPlayerCosts = _request.AllPlayers.Select(p => p.Cost).ToList();
        var teamCost = LinearExpr.WeightedSum(model.Selections.Select(p => p.SquadSelected), allPlayerCosts);
        var budgetRemaining = _request.Budget - teamCost;
        model.Maximize(predictedPointsForTeam - transferPenalty + budgetRemaining);
    }

    private void AddConstraints(FplTeamTransfersCpModel model)
    {
        AddPositionConstraints(model);

        AddMaxPerTeamConstraint(model);

        AddCostConstraint(model);

        AddStartingTeamConstraints(model);
    }

    private void AddStartingTeamConstraints(FplTeamTransfersCpModel model)
    {
        var teamGoalkeepers = LinearExpr.Sum(model.SelectedGks.Select(p => p.TeamSelected));
        var teamDefenders = LinearExpr.Sum(model.SelectedDefs.Select(p => p.TeamSelected));
        var teamMidfielders = LinearExpr.Sum(model.SelectedMids.Select(p => p.TeamSelected));
        var teamForwards = LinearExpr.Sum(model.SelectedFwds.Select(p => p.TeamSelected));
        model.Add(teamGoalkeepers == 1);
        model.Add(teamDefenders >= _request.Options.MinTeamDefenders);
        model.Add(teamMidfielders >= _request.Options.MinTeamMidfielders);
        model.Add(teamForwards >= _request.Options.MinTeamForwards);

        model.Add(
            teamGoalkeepers + teamDefenders + teamMidfielders + teamForwards == _request.Options.UsefulPlayers);

        foreach (var playerSelectionVar in model.Selections)
        {
            model.Add(playerSelectionVar.SquadSelected >= playerSelectionVar.TeamSelected);
        }
    }

    private void AddPositionConstraints(FplTeamTransfersCpModel model)
    {
        // Sum up the selections for each position, and ensure it matches the number of players required per position.
        model.Add(LinearExpr.Sum(model.SelectedGks.Select(p => p.SquadSelected)) == _request.Options.SquadGoalkeeperCount);
        model.Add(LinearExpr.Sum(model.SelectedDefs.Select(p => p.SquadSelected)) == _request.Options.SquadDefenderCount);
        model.Add(LinearExpr.Sum(model.SelectedMids.Select(p => p.SquadSelected)) == _request.Options.SquadMidfielderCount);
        model.Add(LinearExpr.Sum(model.SelectedFwds.Select(p => p.SquadSelected)) == _request.Options.SquadForwardCount);
    }

    private void AddMaxPerTeamConstraint(FplTeamTransfersCpModel model)
    {
        // Sum up the selections for each team, and ensure it doesn't exceed the maximum number of players per team.
        foreach (var (_, selections) in model.TeamSelectionCounts)
        {
            model.Add(LinearExpr.Sum(selections) <= _request.Options.MaxPlayersPerTeam);
        }
    }

    private void AddCostConstraint(FplTeamTransfersCpModel model)
    {
        // Get the scale product of the selection booleans and multiply by each player's cost.
        var allPlayerCosts = _request.AllPlayers.Select(p => p.Cost).ToList();
        var allPlayerCost = LinearExpr.WeightedSum(model.Selections.Select(s => s.SquadSelected), allPlayerCosts);

        model.Add(allPlayerCost <= _request.Budget);
    }

    private FplPlayerSelectionVar GetSelectionVar(FplTeamTransfersCpModel model, Player player, bool isExisting)
    {
        var selection = new FplPlayerSelectionVar(model, player);

        List<FplPlayerSelectionVar> listOfPlayersToAddTo = player.Position switch
        {
            Position.Goalkeeper => isExisting ? model.ExistingSelectedGks : model.TransferSelectedGks,
            Position.Defender => isExisting ? model.ExistingSelectedDefs : model.TransferSelectedDefs,
            Position.Midfielder => isExisting ? model.ExistingSelectedMids : model.TransferSelectedMids,
            Position.Forward => isExisting ? model.ExistingSelectedFwds : model.TransferSelectedFwds,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (_request.Options.GetRidOf != null && player.Name == _request.Options.GetRidOf.Name)
        {
            model.Add(selection.SquadSelected == 0);
        }
        listOfPlayersToAddTo.Add(selection);

        return selection;
    }
}