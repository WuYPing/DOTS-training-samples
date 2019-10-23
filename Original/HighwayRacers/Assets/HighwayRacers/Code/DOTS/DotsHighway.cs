﻿using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace HighwayRacers
{
    public struct DotsHighway
    {
        struct Section
        {
            public float3 Pos;
            public float Lane0Length;
            public float StartRotation;
            public float CurveRadius; // 0 means straight

            public float LaneLength(float lane)
            {
                return math.select(
                    Lane0Length,
                    (CurveRadius + lane * Highway.LANE_SPACING) * math.PI * 0.5f,
                    CurveRadius > 0);
            }

            public float3 GetLocalPosition(float localDistance, float lane, out float rotY)
            {
                if (CurveRadius == 0)
                {
                    rotY = 0;
                    return new float3(
                        Highway.LANE_SPACING * ((Highway.NUM_LANES - 1) / 2f - lane),
                        0, localDistance);
                }
                float radius = CurveRadius + lane * Highway.LANE_SPACING;
                rotY = localDistance / radius;
                return new float3(
                    Highway.MID_RADIUS - math.cos(rotY) * radius,
                    0, math.sin(rotY) * radius);
            }
        }
        NativeArray<Section> Sections;
        float LastLaneLength;

        public void Create(HighwayPiece[] pieces)
        {
            Dispose();
            Sections = new NativeArray<Section>(pieces.Length, Allocator.Persistent);
            for (int i = 0; i < pieces.Length; ++i)
            {
                var len = pieces[i].length(0);
                Sections[i] = new Section
                {
                    Pos = pieces[i].transform.localPosition,
                    Lane0Length = len,
                    StartRotation = pieces[i].startRotation,
                    CurveRadius = pieces[i].curveRadiusLane0
                };
                Lane0Length += len;
            }
            LastLaneLength = 0;
            for (int s = 0; s < Sections.Length; ++s)
                LastLaneLength += Sections[s].LaneLength(NumLanes - 1);
        }

        public void Dispose()
        {

//            ReaderJob.Complete();
            if (Sections.IsCreated) Sections.Dispose();
        }

//        JobHandle ReaderJob;
        public void RegisterReaderJob(JobHandle h)
        {
  //          ReaderJob = h;
        }

        public float NumLanes { get { return Highway.NUM_LANES; } }

        public float Lane0Length { get; private set; }

        public float LaneLength(float lane)
        {
#if true // GML todo: why?
            float len = 0;
            for (int i = 0; i < Sections.Length; ++i)
                len += Sections[i].LaneLength(lane);
            return len;
#else
            return math.lerp(Lane0Length, LastLaneLength, lane / (NumLanes - 1));
#endif
        }

        /// <summary>
        /// Wraps distance to be in [0, l), where l is the length of the given lane.
        /// </summary>
        public float WrapDistance(float distance, float lane)
        {
            return distance % LaneLength(lane);
        }

        /// <summary>
        /// Gets world position of a car based on its lane and distance from the start in that lane.
        /// </summary>
        /// <param name="distance"></param>
        /// <param name="lane"></param>
        /// <param name="x"></param>
        /// <param name="z"></param>
        /// <param name="rotation">y rotation of the car, in radians.</param>
        public void GetWorldPosition(
            float distance, float lane, out float3 outPos, out quaternion outRotation)
        {
            // keep distance in [0, length)
            distance = WrapDistance(distance, lane);

            float pieceStartDistance = 0;
            float pieceEndDistance = 0;
            for (int i = 0; i < Sections.Length; i++)
            {
                var section = Sections[i];
                pieceStartDistance = pieceEndDistance;
                pieceEndDistance += section.LaneLength(lane);
                if (distance < pieceEndDistance)
                {
                    // inside section i
                    var localPos = section.GetLocalPosition(
                        distance - pieceStartDistance, lane, out float rotY);

                    // transform
                    var q = quaternion.AxisAngle(
                        new float3(0, 1, 0), section.StartRotation);
                    outPos = math.mul(q, localPos) + section.Pos;
                    outRotation = quaternion.AxisAngle(
                        new float3(0, 1, 0), rotY + section.StartRotation);
                    return;
                }
            }
            outPos = float3.zero;
            outRotation = quaternion.identity;
        }

        /// <summary>
        /// Gets distance in another lane that appears to be the same distance in the given lane.
        /// </summary>
        public float GetEquivalentDistance(float distance, float lane, float otherLane)
        {
            // keep distance in [0, length)
			distance = WrapDistance(distance, lane);

            float pieceStartDistance = 0;
            float pieceEndDistance = 0;
            float pieceStartDistanceOtherLane = 0;
            float pieceEndDistanceOtherLane = 0;

            for (int i = 0; i < Sections.Length; i++)
            {
                var section = Sections[i];
                pieceStartDistance = pieceEndDistance;
                pieceStartDistanceOtherLane = pieceEndDistanceOtherLane;
                pieceEndDistance += section.LaneLength(lane);
                pieceEndDistanceOtherLane += section.LaneLength(otherLane);
                if (distance < pieceEndDistance)
                {
                    // inside piece i
                    if (section.CurveRadius == 0)
                        return pieceStartDistanceOtherLane + distance - pieceStartDistance;
                    // curved piece
                    float radius = section.CurveRadius + Highway.LANE_SPACING * lane;
                    float radiusOtherLane = section.CurveRadius + Highway.LANE_SPACING * otherLane;
                    return pieceStartDistanceOtherLane
                        + (distance - pieceStartDistance) * radiusOtherLane / radius;
                }
            }
            return 0;
        }

        // TODO: Hack - temporary methods to create/destroy cars
        public void SetNumCars(int numCars)
        {
            var em = World.Active?.EntityManager;
            if (em == null)
                return;
            var query = em.CreateEntityQuery(typeof(CarState));
            var numExistingCars = query.CalculateEntityCount();
            int delta = numCars - numExistingCars;
            if (delta > 0)
                AddCarEntities(delta, em);
            else if (delta < 0)
            {
                var entities = query.ToEntityArray(Allocator.TempJob);
                for (int i = 0; i < delta; ++i)
                    em.DestroyEntity(entities[i]);
                entities.Dispose();
            }
        }

        static int NextCarId = 1;
        Random m_Random;
        void AddCarEntities(int count, EntityManager em)
        {
            float lane = 0;
            m_Random.InitState();
            for (int i = 0; i < count; i++)
            {
                var entity = World.Active.EntityManager.CreateEntity();

                em.AddComponentData(entity,new CarID { Value = NextCarId++ });
                var data = new CarSettings()
                {
                    DefaultSpeed = m_Random.NextFloat(Game.instance.defaultSpeedMin, Game.instance.defaultSpeedMax),
                    OvertakePercent = m_Random.NextFloat(Game.instance.overtakePercentMin, Game.instance.overtakePercentMax),
                    LeftMergeDistance = m_Random.NextFloat(Game.instance.leftMergeDistanceMin, Game.instance.leftMergeDistanceMax),
                    MergeSpace = m_Random.NextFloat(Game.instance.mergeSpaceMin, Game.instance.mergeSpaceMax),
                    OvertakeEagerness = m_Random.NextFloat(Game.instance.overtakeEagernessMin, Game.instance.overtakeEagernessMax),
                };
                em.AddComponentData(entity,data);

                em.AddComponentData(entity,new CarState
                {
                    TargetFwdSpeed = data.DefaultSpeed,
                    FwdSpeed = data.DefaultSpeed,
                    LeftSpeed = 0,

                    PositionOnTrack = m_Random.NextFloat(0, Lane0Length),
                    Lane = lane,
                    TargetLane = 0,
                    CurrentState = CarState.State.NORMAL

                });
                em.AddComponentData(entity,new ColorComponent());
                em.AddComponentData(entity,new ProximityData());
                em.AddComponentData(entity,new Translation());
                em.AddComponentData(entity,new Rotation());
                em.AddComponentData(entity, new LocalToWorld());

                lane += 1;
                if (lane == NumLanes)
                    lane = 0;
            }

        }

    }
}

