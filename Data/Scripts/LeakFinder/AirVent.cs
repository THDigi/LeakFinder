using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Digi.LeakFinder
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_AirVent), useEntityUpdate: false)]
    public class AirVent : MyGameLogicComponent
    {
        // HACK: hardcoded from MyAirVent.VentDummy
        public const string AirVent_DummyName = "vent_001";

        IMyAirVent Block;
        bool DummyIsSet = false;
        public Vector3 DummyLocalPosition;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = Entity as IMyAirVent;
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated)
                    return;

                TerminalControls.Setup();

                if(Block?.CubeGrid?.Physics == null)
                    return; // ignore ghost/projected grids

                Block.AppendingCustomInfo += CustomInfo;

                var internalBlock = (MyCubeBlock)Entity;
                internalBlock.OnModelRefresh += OnModelRefresh;

                NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void OnModelRefresh(MyEntity _)
        {
            try
            {
                // needs to have primary model to get dummy
                if(!DummyIsSet && Block.SlimBlock.ComponentStack.IsBuilt)
                {
                    DummyIsSet = true;

                    Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
                    IMyModelDummy dummy;
                    if(Block.Model.GetDummies(dummies) > 0 && dummies.TryGetValue(AirVent_DummyName, out dummy))
                    {
                        DummyLocalPosition = dummy.Matrix.Translation;
                    }
                    dummies.Clear();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        bool _roomSealed;
        int _roomSealedExpires;
        public bool IsRoomSealed()
        {
            if(MyAPIGateway.Session.IsServer)
                return Block.CanPressurize;

            // HACK: cannot use CanPressurize net-client-side because it uses grid GasSystem that is only available serverside.

            int tick = MyAPIGateway.Session.GameplayFrameCounter;

            if(_roomSealedExpires > tick)
                return _roomSealed;

            Block.SetDetailedInfoDirty();
            string detailedInfo = Block.DetailedInfo;

            if(string.IsNullOrEmpty(detailedInfo))
                return false; // unknown state, assume not pressurized

            string notPressurized = MyTexts.GetString(MySpaceTexts.Oxygen_NotPressurized);
            _roomSealed = !detailedInfo.Contains(notPressurized);
            _roomSealedExpires = tick + (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 1;

            return _roomSealed;
        }

        static void CustomInfo(IMyTerminalBlock block, StringBuilder str)
        {
            try
            {
                IMyAirVent vent = (IMyAirVent)block;
                AirVent logic = block.GameLogic.GetAs<AirVent>();

                if(logic == null)
                    return;

                str.Append('\n');
                str.Append("Air leak scan status:\n");

                LeakInfo leakInfo = LeakFinderMod.Instance.LeakInfo;
                if(leakInfo == null)
                {
                    str.Append("Not initialized.");
                    return;
                }

                if(!leakInfo.Enabled)
                {
                    str.Append("Disabled.");
                    return;
                }

                switch(leakInfo.Status)
                {
                    case InfoStatus.None:
                        if(!vent.IsWorking)
                            str.Append("Air vent not working.");
                        else if(logic.IsRoomSealed())
                            str.Append("Area is sealed.");
                        else
                            str.Append("Ready.");
                        break;
                    case InfoStatus.Computing:
                        str.Append("Computing...");
                        break;
                    case InfoStatus.Drawing:
                        if(leakInfo.UsedFromVent != null && leakInfo.UsedFromVent.CubeGrid != block.CubeGrid)
                            str.Append("Leak found and displayed on another grid.");
                        else
                            str.Append("Leak found and displayed.");
                        break;
                }

                str.Append("\n");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}