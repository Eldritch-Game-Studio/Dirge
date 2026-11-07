#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;

namespace Dirge.Tool.Browser;

public sealed partial class BrowserPane
{
    private bool Matches(string name) => _query.Length == 0 || name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---- snapshots --------------------------------------------------------------------------------------------------------

    private void BuildSnapshots()
    {
        List<Snapshot> snapshots = _ctx!.Project.Project.Snapshots;
        int shown = 0;
        for (int i = 0; i < snapshots.Count; i++)
        {
            Snapshot s = snapshots[i];
            if (!Matches(s.Name)) continue;
            HTMLElement row = AddRow(_tree, "snapshot", 0, s.Name, s.Mode + " · " + s.Priority, "");
            row.SetAttribute("data-id", s.Id.ToString());
            shown++;
        }
        if (shown == 0) AddEmptyRow(_query.Length > 0 ? "No snapshots match" : "No snapshots — + Snapshot");
    }

    public void NewSnapshot()
    {
        if (_ctx is null) return;
        List<Snapshot> list = _ctx.Project.Project.Snapshots;
        var snapshot = new Snapshot { Name = UniqueName(list.ConvertAll(s => s.Name), "New Snapshot") };
        _ctx.Project.Mutate("New snapshot", ChangeScope.Snapshots, () => list.Add(snapshot), () => list.Remove(snapshot), snapshot.Id);
        _ctx.Selection.Select(SelectionKind.Snapshot, snapshot.Id);
        StartRenameOf("snapshot", snapshot.Id.ToString());
    }

    public void RenameSnapshot(DirgeId id, string name)
    {
        if (_ctx is null) return;
        Snapshot? snapshot = _ctx.Project.Project.Snapshots.Find(s => s.Id == id);
        if (snapshot is null || snapshot.Name == name) return;
        string old = snapshot.Name;
        _ctx.Project.Mutate("Rename snapshot", ChangeScope.Snapshots, () => snapshot.Name = name, () => snapshot.Name = old, id);
    }

    public void DeleteSnapshot(DirgeId id)
    {
        if (_ctx is null) return;
        List<Snapshot> list = _ctx.Project.Project.Snapshots;
        Snapshot? snapshot = list.Find(s => s.Id == id);
        if (snapshot is null) return;
        Confirm("Delete snapshot \"" + snapshot.Name + "\"?", () =>
        {
            int index = list.IndexOf(snapshot);
            _ctx.Project.Mutate("Delete snapshot", ChangeScope.Snapshots, () => list.Remove(snapshot), () => list.Insert(Math.Min(index, list.Count), snapshot), id);
            if (_ctx.Selection.Kind == SelectionKind.Snapshot && _ctx.Selection.Id == id) _ctx.Selection.Clear();
        });
    }

    // ---- global parameters ---------------------------------------------------------------------------------------------

    private void BuildParameters()
    {
        List<ParameterDefinition> parameters = _ctx!.Project.Project.GlobalParameters;
        int shown = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            ParameterDefinition p = parameters[i];
            if (!Matches(p.Name)) continue;
            string meta = p.Kind == ParameterKind.Labeled
                ? p.Labels.Count + " labels"
                : Dom.F(p.Minimum, p.Kind == ParameterKind.Discrete ? 0 : 2) + " … " + Dom.F(p.Maximum, p.Kind == ParameterKind.Discrete ? 0 : 2);
            if (p.BuiltIn != BuiltInParameter.None) meta = p.BuiltIn + " · " + meta;
            HTMLElement row = AddRow(_tree, "parameter", 0, p.Name, meta, "");
            row.SetAttribute("data-id", p.Id.ToString());
            shown++;
        }
        if (shown == 0) AddEmptyRow(_query.Length > 0 ? "No parameters match" : "No global parameters — + Parameter");
    }

    public void NewParameter()
    {
        if (_ctx is null) return;
        List<ParameterDefinition> list = _ctx.Project.Project.GlobalParameters;
        var parameter = new ParameterDefinition { Name = UniqueName(list.ConvertAll(p => p.Name), "New Parameter") };
        _ctx.Project.Mutate("New global parameter", ChangeScope.GlobalParameters, () => list.Add(parameter), () => list.Remove(parameter), parameter.Id);
        _ctx.Selection.Select(SelectionKind.Parameter, parameter.Id);
        StartRenameOf("parameter", parameter.Id.ToString());
    }

    public void RenameParameter(DirgeId id, string name)
    {
        if (_ctx is null) return;
        ParameterDefinition? parameter = _ctx.Project.Project.GlobalParameters.Find(p => p.Id == id);
        if (parameter is null || parameter.Name == name) return;
        string old = parameter.Name;
        _ctx.Project.Mutate("Rename global parameter", ChangeScope.GlobalParameters, () => parameter.Name = name, () => parameter.Name = old, id);
    }

    public void DeleteParameter(DirgeId id)
    {
        if (_ctx is null) return;
        List<ParameterDefinition> list = _ctx.Project.Project.GlobalParameters;
        ParameterDefinition? parameter = list.Find(p => p.Id == id);
        if (parameter is null) return;
        Confirm("Delete global parameter \"" + parameter.Name + "\"? Automation and conditions referencing it will report validation errors.", () =>
        {
            int index = list.IndexOf(parameter);
            _ctx.Project.Mutate("Delete global parameter", ChangeScope.GlobalParameters, () => list.Remove(parameter), () => list.Insert(Math.Min(index, list.Count), parameter), id);
            if (_ctx.Selection.Kind == SelectionKind.Parameter && _ctx.Selection.Id == id) _ctx.Selection.Clear();
        });
    }

    // ---- banks --------------------------------------------------------------------------------------------------------

    private void BuildBanks()
    {
        List<AudioBank> banks = _ctx!.Project.Project.Banks;
        int shown = 0;
        bool selectedExists = false;
        for (int i = 0; i < banks.Count; i++)
        {
            AudioBank bank = banks[i];
            if (bank.Id == _selectedBank) selectedExists = true;
            if (!Matches(bank.Name)) continue;
            HTMLElement row = AddRow(_tree, "bank", 0, bank.Name, bank.Events.Count + " events", "");
            row.SetAttribute("data-id", bank.Id.ToString());
            shown++;
        }
        if (!selectedExists) _selectedBank = DirgeId.None;
        if (shown == 0) AddEmptyRow(_query.Length > 0 ? "No banks match" : "No banks — + Bank");
        BuildBankMembers();
    }

    /// <summary>Lower list: every event with a checkbox for membership in the selected bank.</summary>
    private void BuildBankMembers()
    {
        ClearChildren(_memberList);
        AudioProject project = _ctx!.Project.Project;
        AudioBank? bank = project.Banks.Find(b => b.Id == _selectedBank);
        Element heading = _members.QuerySelector(".members-heading")!;
        if (bank is null)
        {
            Dom.SetText(heading, "Select a bank to edit its events");
            return;
        }
        Dom.SetText(heading, "Events in \"" + bank.Name + "\"");
        var sorted = new List<AudioEvent>(project.Events);
        sorted.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < sorted.Count; i++)
        {
            AudioEvent ev = sorted[i];
            bool member = bank.Events.Contains(ev.Id);
            HTMLElement row = Dom.Add(_memberList, "div", "node member d0");
            row.SetAttribute("data-kind", "member");
            row.SetAttribute("data-id", ev.Id.ToString());
            Dom.Add(row, "div", member ? "check on" : "check");
            Dom.Add(row, "p", "name", ev.Path);
        }
        if (sorted.Count == 0)
        {
            HTMLElement row = Dom.Add(_memberList, "div", "node empty d0");
            Dom.Add(row, "p", "name", "No events in project");
        }
    }

    private void OnMemberClick(NecronomiDom.Dom.Event e)
    {
        if (_ctx is null) return;
        Element? row = Dom.Closest(e, ".node.member");
        if (row is null) return;
        AudioBank? bank = _ctx.Project.Project.Banks.Find(b => b.Id == _selectedBank);
        if (bank is null) return;
        DirgeId eventId = DirgeId.Parse(row.GetAttribute("data-id")!);
        ToggleBankMembership(bank, eventId);
    }

    public void ToggleBankMembership(AudioBank bank, DirgeId eventId)
    {
        if (_ctx is null) return;
        int index = bank.Events.IndexOf(eventId);
        if (index >= 0)
        {
            _ctx.Project.Mutate("Remove event from bank", ChangeScope.Events, () => bank.Events.Remove(eventId), () => bank.Events.Insert(Math.Min(index, bank.Events.Count), eventId), bank.Id);
        }
        else
        {
            _ctx.Project.Mutate("Add event to bank", ChangeScope.Events, () => { if (!bank.Events.Contains(eventId)) bank.Events.Add(eventId); }, () => bank.Events.Remove(eventId), bank.Id);
        }
    }

    public void NewBank()
    {
        if (_ctx is null) return;
        List<AudioBank> list = _ctx.Project.Project.Banks;
        var bank = new AudioBank { Name = UniqueName(list.ConvertAll(b => b.Name), "New Bank") };
        _selectedBank = bank.Id;
        _ctx.Project.Mutate("New bank", ChangeScope.Events, () => list.Add(bank), () => list.Remove(bank), bank.Id);
        _ctx.Selection.Select(SelectionKind.Bank, bank.Id);
        StartRenameOf("bank", bank.Id.ToString());
    }

    public void RenameBank(DirgeId id, string name)
    {
        if (_ctx is null) return;
        AudioBank? bank = _ctx.Project.Project.Banks.Find(b => b.Id == id);
        if (bank is null || bank.Name == name) return;
        string old = bank.Name;
        _ctx.Project.Mutate("Rename bank", ChangeScope.Events, () => bank.Name = name, () => bank.Name = old, id);
    }

    public void DeleteBank(DirgeId id)
    {
        if (_ctx is null) return;
        List<AudioBank> list = _ctx.Project.Project.Banks;
        AudioBank? bank = list.Find(b => b.Id == id);
        if (bank is null) return;
        Confirm("Delete bank \"" + bank.Name + "\"? Events are kept.", () =>
        {
            int index = list.IndexOf(bank);
            _ctx.Project.Mutate("Delete bank", ChangeScope.Events, () => list.Remove(bank), () => list.Insert(Math.Min(index, list.Count), bank), id);
            if (_selectedBank == id) _selectedBank = DirgeId.None;
            if (_ctx.Selection.Kind == SelectionKind.Bank && _ctx.Selection.Id == id) _ctx.Selection.Clear();
        });
    }

    private static string UniqueName(List<string> existing, string desired)
    {
        if (!existing.Contains(desired)) return desired;
        for (int n = 2; ; n++)
        {
            string candidate = desired + " " + n;
            if (!existing.Contains(candidate)) return candidate;
        }
    }
}
