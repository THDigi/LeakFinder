using System;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Digi.LeakFinder
{
    public static class TerminalControls
    {
        public static void Setup()
        {
            if(LeakFinderMod.Instance.TerminalControl != null)
                return;

            // separator
            MyAPIGateway.TerminalControls.AddControl<IMyAirVent>(MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyAirVent>(string.Empty));

            // on/off switch
            IMyTerminalControlOnOffSwitch c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyAirVent>("FindAirLeak");
            c.Title = MyStringId.GetOrCompute("Leak Finder");
            c.Tooltip = MyStringId.GetOrCompute("Shows you a trail leading towards the outside of the ship."
                                              + "\nIt re-computes automatically when ship is modified."
                                              + "\nAdded by Leak Finder mod.");
            c.OnText = MyStringId.GetOrCompute("Find");
            c.OffText = MyStringId.GetOrCompute("Hide");
            c.Visible = Toggle_Visible;
            c.Enabled = Toggle_Enabled;
            c.SupportsMultipleBlocks = false;
            c.Setter = Toggle_Setter;
            c.Getter = Toggle_Getter;
            MyAPIGateway.TerminalControls.AddControl<IMyAirVent>(c);
            LeakFinderMod.Instance.TerminalControl = c;
        }

        static bool Toggle_Visible(IMyTerminalBlock block)
        {
            return block.GameLogic.GetAs<AirVent>() != null;
        }

        static bool Toggle_Enabled(IMyTerminalBlock block)
        {
            if(MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Session.IsServer)
                return false;

            if(!block.IsWorking)
                return false;

            LeakInfo leakInfo = LeakFinderMod.Instance.LeakInfo;
            if(!leakInfo.Enabled)
                return false;

            AirVent logic = block.GameLogic.GetAs<AirVent>();
            if(logic == null)
                return false;

            return true;
        }

        static void Toggle_Setter(IMyTerminalBlock block, bool v)
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Session.IsServer)
                    return; // nothing to draw DS-side

                LeakInfo leakInfo = LeakFinderMod.Instance.LeakInfo;
                if(!leakInfo.Enabled)
                    return;

                IMyAirVent vent = (IMyAirVent)block;
                AirVent logic = block.GameLogic.GetAs<AirVent>();
                if(logic == null)
                    return;

                if(leakInfo.Status != InfoStatus.None)
                {
                    leakInfo.ClearStatus("manual from terminal");
                }
                else
                {
                    if(!block.IsWorking || logic.IsRoomSealed())
                    {
                        LeakFinderMod.Instance.TerminalControl.UpdateVisual();
                        return;
                    }

                    Vector3I startPosition = block.CubeGrid.WorldToGridInteger(Vector3D.Transform(logic.DummyLocalPosition, block.WorldMatrix));

                    leakInfo.StartThread(vent, startPosition);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        static bool Toggle_Getter(IMyTerminalBlock block)
        {
            LeakInfo leakInfo = LeakFinderMod.Instance.LeakInfo;
            return (leakInfo.Status != InfoStatus.None);
        }
    }
}
