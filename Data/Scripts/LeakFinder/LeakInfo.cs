using System;
using System.Collections.Generic;
using System.Threading;
using Digi.LeakFinder.VanillaData;
using ParallelTasks;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Digi.LeakFinder
{
    public enum InfoStatus { None, Computing, Drawing }

    public class LeakInfo
    {
        public bool Enabled => MyAPIGateway.Session.SessionSettings.EnableOxygenPressurization && MyAPIGateway.Session.SessionSettings.EnableOxygen;

        public bool RunUpdate { get; private set; } = false;
        public bool RunDraw { get; private set; } = false;

        // used in main thread
        public InfoStatus Status { get; private set; } // the scan/thread status
        public IMyAirVent UsedFromVent { get; private set; } // the vent used to start the air leak scan from
        Task Task;
        IMyHudNotification Notify = null;
        const string NotifyFont = MyFontEnum.Debug;
        //int DrawUntilTick;
        bool StopSpawning = false; // used to stop spawning of particles when the first particle reaches the end point and loops back around
        int DelayParticles = 0; // used to count spawning particles in Draw(), isolated from gamelogic (which is affected by pause)
        readonly List<Particle> Particles = new List<Particle>();
        MyConcurrentPool<Particle> ParticlePool = new MyConcurrentPool<Particle>(expectedAllocations: 0);
        ParticleData CurrentParticleData = null;
        int ReleaseMemoryAt = 0; // for memory releasing
        List<IMyDoor> DoorEvents = new List<IMyDoor>();
        int RescanAtTick = 0;

        // used in background thread
        public Vector3I StartPosition { get; private set; }
        BreadcrumbHeap.Crumb Crumb = null;
        readonly BreadcrumbHeap Crumbs = new BreadcrumbHeap();
        readonly HashSet<Vector3I> Scanned = new HashSet<Vector3I>(Vector3I.Comparer);

        // shared between main and background thread but not at the same time.
        IMyCubeGrid SelectedGrid = null;
        readonly List<MyTuple<Vector3I, Vector3I>> DrawLines = new List<MyTuple<Vector3I, Vector3I>>();

        // used in both threads, but I've learned this is safe to assign in main and read in the background thread
        bool CancelTask = false;
        bool TaskProcessing = false;
        bool TaskGeneratingLines = false;
        int TaskComputedCells = 0;

        public const int DrawMinSeconds = 30; // cap for the dynamically calculated particle lifetime
        public const int DrawMaxSeconds = 300;
        public const int DrawFadeOutTicks = (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 3; // fade out particles over this many ticks before they vanish

        public readonly MyStringId ParticleMaterial = MyStringId.GetOrCompute("LeakFinder_Particle");
        public const float EndPointSize = 0.15f;
        public readonly Color StartPointColorOverlay = new Color(0, 255, 0);
        public readonly Color StartPointColorWorld = new Color(0, 155, 0);
        public readonly Color EndPointColorOverlay = new Color(255, 0, 0);
        public readonly Color EndPointColorWorld = new Color(155, 0, 0);

        public const double DrawDepth = 0.01; // for see-through-walls
        public const float DrawDepthF = (float)DrawDepth;

        public const int ReleaseMemoryAfterTicks = (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 60 * 5;

        readonly ParticleData[] ParticleDataGridSize = new ParticleData[]
        {
            new ParticleData(size: 0.1f, spawnDelay: 30, lerpPos: 0.075f, walkSpeed: 0.1f), // largeship
            new ParticleData(size: 0.1f, spawnDelay: 30, lerpPos: 0.25f, walkSpeed: 0.4f) // smallship
        };

        public LeakInfo()
        {
        }

        public void Dispose()
        {
            ClearStatus("LeakInfo.Dispose()");
        }

        public void Update()
        {
            int tick = MyAPIGateway.Session.GameplayFrameCounter;

            if(RescanAtTick > 0 && RescanAtTick <= tick)
            {
                if(Status == InfoStatus.Computing)
                {
                    RescanAtTick += (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 1;
                }
                else
                {
                    RescanAtTick = 0;

                    var vent = UsedFromVent;
                    if(vent != null)
                    {
                        AirVent logic = vent.GameLogic?.GetAs<AirVent>();

                        if(!vent.IsWorking || vent.MarkedForClose || logic == null)
                        {
                            ClearStatus("rescan: vent not working/closed");
                            NotifyHUD("Air vent no longer working", 3000, MyFontEnum.Red);
                        }
                        else if(logic.IsRoomSealed())
                        {
                            ClearStatus("rescan: room sealed");
                            NotifyHUD("No more leaks!", 3000, MyFontEnum.Green);
                        }
                        else
                        {
                            StartThread(UsedFromVent, StartPosition);
                        }
                    }
                    else
                    {
                        // TODO: what happens here?
                    }
                }
            }

            if(tick % 60 == 40 && RescanAtTick == 0 && Status != InfoStatus.None)
            {
                var logic = UsedFromVent?.GameLogic?.GetAs<AirVent>();
                if(logic != null && logic.IsRoomSealed())
                {
                    ClearStatus("periodic room sealed check");
                }
            }

            if(Status == InfoStatus.Computing)
            {
                if(tick % 15 == 0)
                {
                    if(!TaskProcessing)
                    {
                        NotifyHUD("Waiting for available thread.", 1000, NotifyFont);
                    }
                    else if(TaskGeneratingLines)
                    {
                        NotifyHUD("Computing path, finalizing...", 1000, NotifyFont);
                    }
                    else
                    {
                        float totalCells = (SelectedGrid.Max - SelectedGrid.Min).Volume();
                        int progress = (int)Math.Round((TaskComputedCells / totalCells) * 100);
                        NotifyHUD($"Computing path, {progress.ToString()}% of volume walked...", 1000, NotifyFont);
                    }
                }
            }
            else if(Status == InfoStatus.Drawing) // if the path to air leak is shown then decrease the countdown timer or clear it directly if the grid suddenly vanishes.
            {
                //if(DrawUntilTick <= tick)
                //{
                //    ClearStatus();
                //}
            }
            else if(Status == InfoStatus.None)
            {
                if(ReleaseMemoryAt > 0 && ReleaseMemoryAt < tick)
                {
                    ReleaseMemoryAt = 0;

                    Particles.Capacity = 0;
                    ParticlePool = new MyConcurrentPool<Particle>(expectedAllocations: 0);

                    Crumbs.ClearPool();
                }
            }
        }

        void SetStatus(InfoStatus status)
        {
            bool different = (Status != status);

            Status = status;
            RunDraw = (status == InfoStatus.Drawing);

            if(status == InfoStatus.Drawing) // on only, it turns off itself after cleaning
                RunUpdate = true;

            var ventInTerminal = LeakFinderMod.Instance.VentInTerminal;
            if(different && ventInTerminal != null)
            {
                ventInTerminal.RefreshCustomInfo();
                ventInTerminal.SetDetailedInfoDirty();
            }
        }

        /// <summary>
        /// Clears the lines, various status data and gracefully stops the processing thread if running.
        /// </summary>
        public void ClearStatus(string caller)
        {
            if(Status == InfoStatus.Computing) // gracefully cancel the task by telling it to stop doing stuff and then wait for it to finish so we can clear its data.
            {
                CancelTask = true;
                Task.WaitOrExecute(blocking: true);
            }

            if(SelectedGrid != null)
            {
                SelectedGrid.OnMarkForClose -= SelectedGridMarkedForClose;
                SelectedGrid.OnBlockAdded -= GridBlockChanges;
                SelectedGrid.OnBlockRemoved -= GridBlockChanges;
                SelectedGrid.OnBlockIntegrityChanged -= GridBlockChanges;
                SelectedGrid.OnGridSplit -= GridSplitOrMerge;
                SelectedGrid.OnGridMerge -= GridSplitOrMerge;
            }

            foreach(IMyDoor door in DoorEvents)
            {
                door.DoorStateChanged -= DoorStateChanged;
            }
            DoorEvents.Clear();

            SetStatus(InfoStatus.None);

            ReleaseMemoryAt = MyAPIGateway.Session.GameplayFrameCounter + ReleaseMemoryAfterTicks;
            DrawLines.Clear();
            StopSpawning = false;
            //DrawUntilTick = 0;
            Crumb = null;
            SelectedGrid = null;
            CurrentParticleData = null;
            UsedFromVent = null;

            TaskProcessing = false;
            TaskGeneratingLines = false;
            TaskComputedCells = 0;

            foreach(Particle particle in Particles)
            {
                ParticlePool.Return(particle);
            }
            Particles.Clear();
        }

        public void StartThread(IMyAirVent block, Vector3I startPosition)
        {
            ClearStatus("StartThread()");

            if(!Enabled)
                return;

            CancelTask = false;
            UsedFromVent = block;
            SelectedGrid = block.CubeGrid;
            SelectedGrid.OnMarkForClose += SelectedGridMarkedForClose;
            SelectedGrid.OnBlockAdded += GridBlockChanges;
            SelectedGrid.OnBlockRemoved += GridBlockChanges;
            SelectedGrid.OnBlockIntegrityChanged += GridBlockChanges;
            SelectedGrid.OnGridSplit += GridSplitOrMerge;
            SelectedGrid.OnGridMerge += GridSplitOrMerge;

            foreach(IMyDoor door in SelectedGrid.GetFatBlocks<IMyDoor>())
            {
                DoorEvents.Add(door);
                door.DoorStateChanged += DoorStateChanged;
            }

            CurrentParticleData = ParticleDataGridSize[(int)SelectedGrid.GridSizeEnum]; // particle settings for the grid cell size
            StartPosition = startPosition;

            //int gridCells = (SelectedGrid.Max - SelectedGrid.Min).Volume();
            //if(gridCells <= 50000)
            //    Task = MyAPIGateway.Parallel.Start(ThreadRun, ThreadFinished);
            //else
            Task = MyAPIGateway.Parallel.StartBackground(ThreadRun, ThreadFinished);

            SetStatus(InfoStatus.Computing);
            RunUpdate = true;
            NotifyHUD("Computing path...", 1000 * 5, NotifyFont);
        }

        void SelectedGridMarkedForClose(IMyEntity ent)
        {
            ClearStatus("Grid closed");
        }

        void GridSplitOrMerge(IMyCubeGrid gridA, IMyCubeGrid gridB)
        {
            ScheduleRescan();
        }

        void GridBlockChanges(IMySlimBlock slim)
        {
            ScheduleRescan();
        }

        void DoorStateChanged(bool open)
        {
            ScheduleRescan();
        }

        void ScheduleRescan()
        {
            NotifyHUD("Ship changed, rescanning soon...", 1000, MyFontEnum.Debug);
            RescanAtTick = MyAPIGateway.Session.GameplayFrameCounter + (int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * 1;
        }

        #region Pathfinding
        /// <summary>
        /// <para>A* Pathfinding - Credit for the original code to Roy T. - http://roy-t.nl/2011/09/24/another-faster-version-of-a-2d3d-in-c.html </para>
        /// <para>This was modified and adapted to suit the needs of this mod, which changes the specific end vector to an "outside of grid" target.</para>
        /// <para>Also made into a separate thread.</para>
        /// </summary>
        void ThreadRun()
        {
            try
            {
                TaskProcessing = true;

                // generate crumbs and path cost
                Crumb = PathfindGenerateCrumbs(SelectedGrid, StartPosition);

                if(Crumb != null)
                {
                    TaskGeneratingLines = true;

                    while(Crumb.Next != null)
                    {
                        DrawLines.Add(MyTuple.Create(Crumb.Next.Position, Crumb.Position));
                        Crumb = Crumb.Next;
                    }
                }
                else
                {
                    // TODO: what happens here?
                }
            }
            finally
            {
                // cleanup after PathfindGenerateCrumbs()
                Scanned.Clear();
                Crumbs.Clear();
            }
        }

        /// <summary>Find the closest path towards the exit.
        /// <para>Returns null if no path was found.</para>
        /// <para>NOTE: It can return null if <paramref name="start"/> is inside a closed door's position.</para>
        /// </summary>
        BreadcrumbHeap.Crumb PathfindGenerateCrumbs(IMyCubeGrid grid, Vector3I start) // used in a background thread
        {
            Scanned.Clear();
            Crumbs.Clear();
            Crumbs.Add(start);

            Vector3I[] directions = Base6Directions.IntDirections;
            const int DirLen = 6;

            Vector3I min = grid.Min;
            Vector3I max = grid.Max;
            BoundingBoxI gridBB = new BoundingBoxI(min - Vector3I.One, max + Vector3I.One);

            while(Crumbs.ListHead != null) // HACK: HasNext() inlined
            {
                if(CancelTask)
                    return null;

                // HACK: GetFirst() inlined
                BreadcrumbHeap.Crumb crumb = Crumbs.ListHead;
                Crumbs.ListHead = crumb.NextListElem;

                {
                    Vector3I s = Vector3I.Min(max - crumb.Position, crumb.Position - min);
                    if(Math.Min(s.X, Math.Min(s.Y, s.Z)) < 0)
                        return crumb;
                }
                // HACK: manually inlined ^v
                //if(DistanceToBox(crumb.Position, grid.Min, grid.Max) < 0)
                //    return crumb; // found outside of grid, terminate.

                if(!Scanned.Add(crumb.Position))
                    continue;

                Interlocked.Increment(ref TaskComputedCells);

                for(int dirIdx = 0; dirIdx < DirLen; ++dirIdx)
                {
                    Vector3I target = crumb.Position + directions[dirIdx];

                    Vector3I s = Vector3I.Min(max - target, target - min);
                    int distToOutside = Math.Min(s.X, Math.Min(s.Y, s.Z)) + 1; // adding 1 to offset edges
                    // HACK: manually inlined ^v
                    //int distToOutside = DistanceToBox(target, grid.Min, grid.Max) + 1; // adding 1 to offset edges

                    if(distToOutside < 0) // gone outside of edge already, exit
                        return crumb;

                    if(gridBB.Contains(target) == ContainmentType.Contains && !Pressurization.IsAirtightBetweenPositions(grid, crumb.Position, target))
                    {
                        int directionCost = 1; // cost to turn
                        if(crumb.Next != null)
                        {
                            var moveDir = Base6Directions.GetDirection(crumb.Position - crumb.Next.Position);
                            if(moveDir == Base6Directions.EnumDirections[dirIdx])
                                directionCost = 0; // cost to go straight
                        }

                        int pathCost = crumb.PathCost + directionCost; // direction movement cost
                        int cost = pathCost + distToOutside; // using distance to box edge instead of a predefined end

                        Crumbs.Add(target, cost, pathCost, crumb);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// <para>Distance to min/max edges while point is inside the box.</para>
        /// <para>Returns negative when point is outside of the box.</para>
        /// <para>Thanks to Phoera for supplying this :]</para>
        /// </summary>
        //int DistanceToBox(Vector3I pos, Vector3I min, Vector3I max)
        //{
        //    Vector3I s = Vector3I.Min(max - pos, pos - min);
        //    return Math.Min(s.X, Math.Min(s.Y, s.Z));
        //}
        #endregion Pathfinding

        void ThreadFinished()
        {
            try
            {
                if(Task.Exceptions != null && Task.Exceptions.Length > 0)
                {
                    foreach(Exception e in Task.Exceptions)
                    {
                        Log.Error("(Thread) " + e.ToString());
                    }
                }

                if(CancelTask)
                {
                    ClearStatus("task cancelled");

                    NotifyHUD("Cancelled.", 2000, MyFontEnum.Red);
                }
                else if(Crumb == null)
                {
                    ClearStatus("crumb = null / no leaks");

                    NotifyHUD("No leaks!", 2000, MyFontEnum.Green);
                }
                else
                {
                    //int drawSeconds = (int)MathHelper.Clamp((DrawLines.Count * 2 * SelectedGrid.GridSize), DrawMinSeconds, DrawMaxSeconds);
                    //DrawUntilTick = MyAPIGateway.Session.GameplayFrameCounter + ((int)MyEngineConstants.UPDATE_STEPS_PER_SECOND * drawSeconds);

                    SetStatus(InfoStatus.Drawing);

                    //NotifyHUD($"Found leak, path rendered (visible for {drawSeconds.ToString()}s)", 3000, NotifyFont);
                    NotifyHUD($"Found leak, path rendered", 3000, NotifyFont);
                }

                CancelTask = false;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        /// <summary>
        /// Prints a message to the player, repeated calls to this method will overwrite the previous message on the HUD, therefore preventing spam.
        /// </summary>
        void NotifyHUD(string message, int time = 3000, string font = NotifyFont)
        {
            if(time < 1000 && MyParticlesManager.Paused)
                return; // HACK: avoid notification glitching out if showing them continuously when game is paused

            if(Notify == null)
                Notify = MyAPIGateway.Utilities.CreateNotification(string.Empty);

            Notify.Hide(); // required since SE v1.194
            Notify.Text = "Air leak scan: " + message;
            Notify.AliveTime = time;
            Notify.Font = font;
            Notify.Show();
        }

        #region Drawing
        public void Draw()
        {
            if(Status != InfoStatus.Drawing)
                return;

            BoundingSphereD gridSphere = SelectedGrid.WorldVolume;

            MatrixD camMatrix = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D camPos = camMatrix.Translation;

            double distSq = gridSphere.Radius + 100;
            distSq *= distSq;
            if(Vector3D.DistanceSquared(gridSphere.Center, camPos) > distSq)
            {
                NotifyHUD("Too far, hidden.", 4000, MyFontEnum.Red);
                ClearStatus("too far");
                return;
            }

            Vector3D camFw = camMatrix.Forward;
            double dotPosFw = Vector3D.Dot(camPos, camFw);

            float alpha = 1f;
            Color particleColorWorld = LeakFinderMod.Instance.ClientConfig.ParticleWorld;
            Color particleColorOverlay = LeakFinderMod.Instance.ClientConfig.ParticleOverlay;

            //int drawTicksLeft = DrawUntilTick - MyAPIGateway.Session.GameplayFrameCounter;
            //if(drawTicksLeft < DrawFadeOutTicks)
            //{
            //    alpha = (drawTicksLeft / (float)DrawFadeOutTicks);
            //    particleColorWorld *= alpha;
            //    particleColorOverlay *= alpha;
            //}

            // start dot
            Vector3D startPointWorld = SelectedGrid.GridIntegerToWorld(DrawLines[DrawLines.Count - 1].Item1);
            //if(IsVisibleFast(ref camPos, ref camFw, ref startPointWorld))
            // HACK: manually inlined ^v
            if(Vector3D.Dot(camPos, startPointWorld) - dotPosFw > 0)
            {
                MyTransparentGeometry.AddPointBillboard(ParticleMaterial, StartPointColorWorld * alpha, startPointWorld, EndPointSize, 0);

                Vector3D posOverlay = camPos + ((startPointWorld - camPos) * DrawDepth);
                MyTransparentGeometry.AddPointBillboard(ParticleMaterial, StartPointColorOverlay * alpha, posOverlay, EndPointSize * 0.5f * DrawDepthF, 0);
            }

            bool spawnSync = false;

            // timing particles
            if(!MyParticlesManager.Paused && ++DelayParticles > CurrentParticleData.SpawnDelay)
            {
                DelayParticles = 0;

                if(StopSpawning)
                    spawnSync = true;
                else
                    Particles.Add(ParticlePool.Get().Init(SelectedGrid, DrawLines));
            }

            // draw/move particles
            for(int i = Particles.Count - 1; i >= 0; --i)
            {
                DrawReturn result = Particles[i].Draw(SelectedGrid, DrawLines, particleColorWorld, particleColorOverlay, CurrentParticleData, ref camPos, dotPosFw, spawnSync);
                if(result == DrawReturn.StopSpawning)
                    StopSpawning = true;
            }

            // end dot
            Vector3D endPointWorld = SelectedGrid.GridIntegerToWorld(DrawLines[0].Item2);
            //if(IsVisibleFast(ref camPos, ref camFw, ref endPointWorld))
            // HACK: manually inlined ^v
            if(Vector3D.Dot(camPos, endPointWorld) - dotPosFw > 0)
            {
                MyTransparentGeometry.AddPointBillboard(ParticleMaterial, EndPointColorWorld * alpha, endPointWorld, EndPointSize, 0);

                Vector3D posOverlay = camPos + ((endPointWorld - camPos) * DrawDepth);
                MyTransparentGeometry.AddPointBillboard(ParticleMaterial, EndPointColorOverlay * alpha, posOverlay, EndPointSize * 0.5f * DrawDepthF, 0);
            }
        }

        //public static bool IsVisibleFast(ref Vector3D camPos, ref Vector3D camFw, ref Vector3D point)
        //{
        //    // Vector3D.Dot(camPos, point) - Vector3D.Dot(camPos, camFw) > 0
        //    return (((camPos.X * point.X + camPos.Y * point.Y + camPos.Z * point.Z) - (camPos.X * camFw.X + camPos.Y * camFw.Y + camPos.Z * camFw.Z)) > 0);
        //}
        #endregion Drawing
    }
}