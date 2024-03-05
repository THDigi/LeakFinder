using System;
using System.Collections.Generic;
using Digi.LeakFinder.ConfigMenu;
using Draygo.API;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.ModAPI;

namespace Digi.LeakFinder
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class LeakFinderMod : MySessionComponentBase
    {
        public static LeakFinderMod Instance;

        public LeakInfo LeakInfo;
        public ClientConfig ClientConfig;
        public ModConfigMenu ModConfigMenu;

        public HudAPIv2 TextAPI;

        public IMyAirVent VentInTerminal; // used to constantly update the detail info panel of the viewed air vent only
        public IMyTerminalControlOnOffSwitch TerminalControl; // the air vent button, used to refresh its state manually when clicking it doesn't actually turn to Find state.

        int SaveConfigAtTick = 0;

        public override void LoadData()
        {
            Instance = this;

            try
            {
                if(MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Session.IsServer)
                    return;

                MyAPIGateway.TerminalControls.CustomControlGetter += CustomControlGetter;

                LeakInfo = new LeakInfo();
                ClientConfig = new ClientConfig();
                ModConfigMenu = new ModConfigMenu();
                TextAPI = new HudAPIv2(ModConfigMenu.CreateMenu);

                ClientConfig.FirstLoad();

                SetUpdateOrder(MyUpdateOrder.AfterSimulation);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void BeforeStart()
        {
        }

        protected override void UnloadData()
        {
            try
            {
                MyAPIGateway.TerminalControls.CustomControlGetter -= CustomControlGetter;

                LeakInfo?.Dispose();
                TextAPI?.Close();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            finally
            {
                Instance = null;
            }
        }

        public void ScheduleSaveConfig()
        {
            SaveConfigAtTick = MyAPIGateway.Session.GameplayFrameCounter + (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 3;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                int tick = MyAPIGateway.Session.GameplayFrameCounter;

                if(SaveConfigAtTick > 0 && SaveConfigAtTick <= tick)
                {
                    SaveConfigAtTick = 0;
                    ClientConfig.Save();
                }

                if(LeakInfo.RunUpdate)
                {
                    LeakInfo.Update();
                }

                // refresh detailed info for currently viewed vent every second (offset by 40 ticks to not match with other stuff)
                if(tick % 60 == 40)
                {
                    if(VentInTerminal != null)
                    {
                        VentInTerminal.RefreshCustomInfo();
                        VentInTerminal.SetDetailedInfoDirty();
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Draw()
        {
            try
            {
                if(LeakInfo.RunDraw)
                    LeakInfo.Draw();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        /// <summary>
        /// Gets called when you (local script executer) look at a block in the terminal.
        /// If that happens to be an air vent we need to mark it so we constantly update the custom detail info.
        /// </summary>
        void CustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if(block == VentInTerminal)
                return;

            if(VentInTerminal != null)
            {
                VentInTerminal.OnMarkForClose -= ViewedVentMarkedForClose;
            }

            VentInTerminal = (block as IMyAirVent);
            if(VentInTerminal != null)
            {
                VentInTerminal.OnMarkForClose += ViewedVentMarkedForClose;

                VentInTerminal.RefreshCustomInfo();
                VentInTerminal.SetDetailedInfoDirty();
            }
        }

        void ViewedVentMarkedForClose(IMyEntity ent)
        {
            VentInTerminal.OnMarkForClose -= ViewedVentMarkedForClose;
            VentInTerminal = null;
        }
    }
}
