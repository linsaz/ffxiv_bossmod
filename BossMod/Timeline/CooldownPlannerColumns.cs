﻿using ImGuiNET;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BossMod
{
    // a set of action-use columns that represent cooldown plan
    public class CooldownPlannerColumns : Timeline.ColumnGroup
    {
        private CooldownPlan _plan;
        private Action _onModified;
        private StateMachineTree _tree;
        private List<int> _phaseBranches;
        private bool _syncTimings;
        private string _name = "";
        private StateMachineTimings _timings = new();
        private List<ColumnPlannerTrackCooldown> _colCooldowns = new();
        private List<ColumnPlannerTrackStrategy> _colStrategy = new();
        private Dictionary<ActionID, int> _aidToColCooldown = new();

        private float _trackWidth = 50;

        public Class PlanClass => _plan.Class;

        public CooldownPlannerColumns(CooldownPlan plan, Action onModified, Timeline timeline, StateMachineTree tree, List<int> phaseBranches, ModuleRegistry.Info? moduleInfo, bool syncTimings)
            : base(timeline)
        {
            _plan = plan;
            _onModified = onModified;
            _tree = tree;
            _phaseBranches = phaseBranches;
            _syncTimings = syncTimings;
            var classDef = PlanDefinitions.Classes[plan.Class];
            foreach (var track in classDef.CooldownTracks)
            {
                ActionID defaultAction = new();
                foreach (var a in track.Actions.Where(a => a.minLevel <= plan.Level))
                {
                    _aidToColCooldown[a.aid] = _colCooldowns.Count;
                    if (!defaultAction)
                        defaultAction = a.aid;
                }

                if (defaultAction)
                {
                    var col = Add(new ColumnPlannerTrackCooldown(timeline, tree, phaseBranches, track.Name, moduleInfo, classDef, track, defaultAction, plan.Level));
                    col.Width = _trackWidth;
                    col.NotifyModified = onModified;
                    _colCooldowns.Add(col);
                }
            }
            foreach (var track in classDef.StrategyTracks)
            {
                var col = Add(new ColumnPlannerTrackStrategy(timeline, tree, phaseBranches, track.Name, classDef, track));
                col.Width = _trackWidth;
                col.NotifyModified = onModified;
                _colStrategy.Add(col);
            }

            ExtractPlanData(plan);
        }

        // TODO: should be removed...
        public ColumnPlannerTrackCooldown? TrackForAction(ActionID aid)
        {
            var index = _aidToColCooldown.GetValueOrDefault(aid, -1);
            return index >= 0 ? _colCooldowns[index] : null;
        }

        public void DrawCommonControls()
        {
            if (ImGui.Button("导出至剪切板"))
                ExportToClipboard();
            ImGui.SameLine();
            if (ImGui.Button("从剪切板导入"))
                ImportFromClipboard();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputText("名称", ref _name, 255))
                _onModified();
        }

        public int DrawPhaseControls(int selectedPhase)
        {
            if (ImGui.Button("<##Phase") && selectedPhase > 0)
                --selectedPhase;
            ImGui.SameLine();
            ImGui.TextUnformatted($"当前阶段: {selectedPhase + 1}/{_tree.Phases.Count}");
            ImGui.SameLine();
            if (ImGui.Button(">##Phase") && selectedPhase < _tree.Phases.Count - 1)
                ++selectedPhase;

            var selPhase = _tree.Phases[selectedPhase];
            ImGui.SameLine();
            if (ImGui.SliderFloat("###phase-duration", ref selPhase.Duration, 0, selPhase.MaxTime, $"{selPhase.Name}: %.1f"))
            {
                _timings.PhaseDurations[selectedPhase] = selPhase.Duration;
                if (_syncTimings)
                    _tree.ApplyTimings(_timings);
                _onModified();
            }

            ImGui.SameLine();
            if (ImGui.Button("<##Branch") && _phaseBranches[selectedPhase] > 0)
                --_phaseBranches[selectedPhase];
            ImGui.SameLine();
            ImGui.TextUnformatted($"当前分支: {_phaseBranches[selectedPhase] + 1}/{selPhase.StartingNode.NumBranches}");
            ImGui.SameLine();
            if (ImGui.Button(">##Branch") && _phaseBranches[selectedPhase] < selPhase.StartingNode.NumBranches - 1)
                ++_phaseBranches[selectedPhase];

            return selectedPhase;
        }

        public void DrawConfig()
        {
            DrawCommonControls();
            // TODO: hide/show for tracks
        }

        public void UpdateEditedPlan()
        {
            var plan = BuildPlan();
            _plan.Name = plan.Name;
            _plan.Timings = plan.Timings;
            _plan.Actions = plan.Actions;
            _plan.StrategyOverrides = plan.StrategyOverrides;
        }

        public void ExportToClipboard()
        {
            ImGui.SetClipboardText(JObject.FromObject(BuildPlan()).ToString());
        }

        public void ImportFromClipboard()
        {
            try
            {
                var plan = JObject.Parse(ImGui.GetClipboardText()).ToObject<CooldownPlan>();
                if (plan != null && plan.Class == _plan.Class)
                {
                    ExtractPlanData(plan);
                    _onModified();
                }
                else
                {
                    Service.Log($"Failed to import: plan belong to {plan?.Class} instead of {_plan.Class}");
                }
            }
            catch (Exception ex)
            {
                Service.Log($"Failed to import: {ex}");
            }
        }

        private void ExtractPlanData(CooldownPlan plan)
        {
            _name = plan.Name;

            _timings = plan.Timings.Clone();
            for (int i = _timings.PhaseDurations.Count; i < _tree.Phases.Count; ++i)
                _timings.PhaseDurations.Add(_tree.Phases[i].Duration);
            if (_syncTimings)
                _tree.ApplyTimings(plan.Timings);

            foreach (var col in _colCooldowns)
                while (col.Elements.Count > 0)
                    col.RemoveElement(0);
            foreach (var a in plan.Actions)
            {
                var col = TrackForAction(a.ID);
                if (col != null)
                {
                    var state = _tree.Nodes.GetValueOrDefault(a.StateID);
                    if (state != null)
                    {
                        col.AddElement(state, a.TimeSinceActivation, a.WindowLength, a.ID, a.LowPriority, a.Target.Clone(), a.Comment);
                    }
                }
            }

            foreach (var (col, overrides) in _colStrategy.Zip(plan.StrategyOverrides))
            {
                while (col.Elements.Count > 0)
                    col.RemoveElement(0);

                foreach (var o in overrides)
                {
                    var state = _tree.Nodes.GetValueOrDefault(o.StateID);
                    if (state != null)
                    {
                        col.AddElement(state, o.TimeSinceActivation, o.WindowLength, o.Value, o.Comment);
                    }
                }
            }
        }

        private CooldownPlan BuildPlan()
        {
            var res = new CooldownPlan(_plan.Class, _plan.Level, _name);
            res.Timings = _timings.Clone();
            foreach (var col in _colCooldowns)
            {
                foreach (var e in col.Elements)
                {
                    var cast = (ColumnPlannerTrackCooldown.ActionElement)e;
                    res.Actions.Add(new(cast.Action, e.Window.AttachNode.State.ID, e.Window.Delay, e.Window.Duration, cast.LowPriority, cast.Target.Clone(), cast.Comment));
                }
            }
            foreach (var (col, overrides) in _colStrategy.Zip(res.StrategyOverrides))
            {
                foreach (var e in col.Elements)
                {
                    var cast = (ColumnPlannerTrackStrategy.OverrideElement)e;
                    overrides.Add(new(cast.Value, e.Window.AttachNode.State.ID, e.Window.Delay, e.Window.Duration, cast.Comment));
                }
            }
            return res;
        }
    }
}
