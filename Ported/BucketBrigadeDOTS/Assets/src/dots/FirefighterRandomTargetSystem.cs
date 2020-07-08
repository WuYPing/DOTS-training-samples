﻿using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;

public class FirefighterRandomTargetSystem : SystemBase
{
    private Random m_Random;
    private EntityCommandBufferSystem m_ECBSystem;

    protected override void OnCreate()
    {
        m_Random = new Random(0x1234567);
        m_ECBSystem = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
    }

    protected override void OnUpdate()
    {
        var ecb = m_ECBSystem.CreateCommandBuffer();
        var random = m_Random;

        Entities.WithNone<Target>().ForEach((Entity entity, Firefighter firefighter) =>
        {
            float2 pos = random.NextFloat2() * 10;
            ecb.AddComponent<Target>(entity, new Target{ Value = pos });
        }).Schedule();

        m_ECBSystem.AddJobHandleForProducer(Dependency);
    }
}
