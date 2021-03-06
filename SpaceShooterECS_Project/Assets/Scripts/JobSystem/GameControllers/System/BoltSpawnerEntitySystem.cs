﻿using System;
using Unity.Jobs;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace ECS_SpaceShooterDemo
{
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(EntityManagementGroup))]
    [UpdateAfter(typeof(DestroyEntitySystem))]
    public class BoltSpawnerEntitySystem : GameControllerJobComponentSystem
    {
        //queues that will be used by other system to tell this system to spawn new bolts
        public NativeQueue<Entity> aiBoltSpawnQueue;
        public NativeQueue<Entity> playerBoltSpawnQueue;

        //List used to store entities we need to spawn bolts from
        //Filled each frame from the previous queues after testing if the entities are still valid
        private NativeList<Entity> aiBoltSpawnList;
        private NativeList<Entity> playerBoltSpawnList;

        //entity used by other systems to find the previous queues
        private Entity dataEntity;

        //entities that we will use as "prefab" for our bolts
        Entity prefabEnemyBolt;
        Entity prefabAllyBolt;
        Entity prefabPlayerBolt;

        //Jobs that will go over all newly spawned bolt and set their BoltMoveData values
        [ComputeJobOptimizationAttribute(Accuracy.Med, Support.Relaxed)]
        struct SetAIBoltMoveDataJob : IJobParallelFor
        {
            //List of entities we spawned from
            [ReadOnly]
            public NativeList<Entity> spawningFromEntityList;

            //All the newly spawned bolt entities,
            //the previous list and this array index are aligned
            //meaning spawningFromEntityList[0] is the entity that spawnedBoltEntityArray[0] spawned from
            [ReadOnly]
            [DeallocateOnJobCompletionAttribute]
            public NativeArray<Entity> spawnedBoltEntityArray;

            //ComponentDataFromEntity is used to get component data from specific entities inside job
            [ReadOnly]
            public ComponentDataFromEntity<AISpawnBoltData> aiSpawnBoltDataFromEntity;

            //We need to tell the safety system to allow us to write in a parallel for job
            //This is safe in this case because we are accessing unique entity in each execute call (newly spawned entities)
            [NativeDisableParallelForRestriction]
            public ComponentDataFromEntity<BoltMoveData> boldMoveDataFromEntity;

            public void Execute(int index)
            {
                //Get the spawning information from the entity we spawned from
                AISpawnBoltData spawnBoltData = aiSpawnBoltDataFromEntity[spawningFromEntityList[index]];
                //Get our BoltMoveData
                BoltMoveData boldMoveData = boldMoveDataFromEntity[spawnedBoltEntityArray[index]];

                //Set our initial BoltMoveData values
                boldMoveData.position = spawnBoltData.spawnPosition;
                boldMoveData.forwardDirection = spawnBoltData.spawnDirection;

                boldMoveDataFromEntity[spawnedBoltEntityArray[index]] = boldMoveData;
            }
        }
        protected override void OnCreateManager(int capacity)
        {
            base.OnCreateManager(capacity);

            //Create our queues to hold entities to spawn bolt from
            aiBoltSpawnQueue = new NativeQueue<Entity>(Allocator.Persistent);
            playerBoltSpawnQueue = new NativeQueue<Entity>(Allocator.Persistent);

            aiBoltSpawnList = new NativeList<Entity>(100000, Allocator.Persistent);
            playerBoltSpawnList = new NativeList<Entity>(100000, Allocator.Persistent);

            //Create the entitie that holds our queue, one way of making them accessible to other systems 
            BoltSpawnerEntityData data = new BoltSpawnerEntityData();
            data.aiBoltSpawnQueueConcurrent = aiBoltSpawnQueue;
            data.playerBoltSpawnQueueConcurrent = playerBoltSpawnQueue;

            dataEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(dataEntity, data);

            //Create entities that we will use as "prefab" for our bolts
            //Add the EntityPrefabData IComponentData to make sure those entities are not picked up by systems
            prefabEnemyBolt = EntityManager.Instantiate(MonoBehaviourECSBridge.Instance.enemyBolt);
            EntityManager.AddComponentData<EntityPrefabData>(prefabEnemyBolt, new EntityPrefabData());

            prefabAllyBolt = EntityManager.Instantiate(MonoBehaviourECSBridge.Instance.allyBolt);
            EntityManager.AddComponentData<EntityPrefabData>(prefabAllyBolt, new EntityPrefabData());

            prefabPlayerBolt = EntityManager.Instantiate(MonoBehaviourECSBridge.Instance.playerBolt);
            EntityManager.AddComponentData<EntityPrefabData>(prefabPlayerBolt, new EntityPrefabData());
        }

        protected override void OnDestroyManager()
        {
            //Dispose of queues and lists we allocated
            aiBoltSpawnQueue.Dispose();
            playerBoltSpawnQueue.Dispose();

            aiBoltSpawnList.Dispose();
            playerBoltSpawnList.Dispose();

            //Make sure we destroy entities we are managing
            EntityManager.DestroyEntity(dataEntity);

            EntityManager.DestroyEntity(prefabEnemyBolt);
            EntityManager.DestroyEntity(prefabAllyBolt);
            EntityManager.DestroyEntity(prefabPlayerBolt);

            base.OnDestroyManager();
        }



        JobHandle SpawnBoltFromEntityList(NativeList<Entity> entityList, Entity prefabEntity, bool isboltFromPlayerList, JobHandle jobDepency)
        {
            JobHandle jobDepencyToReturn = jobDepency;

            if (entityList.Length == 0)
            {
                return jobDepencyToReturn;
            }

            UnityEngine.Profiling.Profiler.BeginSample("SpawnBoltFromEntityList");

            Entity boltCopy = EntityManager.Instantiate(prefabEntity);
            EntityManager.RemoveComponent<EntityPrefabData>(boltCopy);

            //Allocate the amount of entities we need in one shot
            NativeArray<Entity> newSpawnedBoltEntityArray = new NativeArray<Entity>(entityList.Length, Allocator.TempJob);
            EntityManager.Instantiate(boltCopy, newSpawnedBoltEntityArray);

            EntityManager.DestroyEntity(boltCopy);

            //If the bolts are from players we just set the BoltMoveData directly, they are not enough generated to warrant creating a job
            if (isboltFromPlayerList)
            {
                //For players bolt just set the new bolt data directly
                //(the cost of starting a job is not work the low amount of data to set)
                for (int i = 0; i < entityList.Length; i++)
                {
                    PlayerSpawnBoltData spawnBoltData = EntityManager.GetComponentData<PlayerSpawnBoltData>(entityList[i]);
                    BoltMoveData moveData = EntityManager.GetComponentData<BoltMoveData>(newSpawnedBoltEntityArray[i]);
                    moveData.position = spawnBoltData.spawnPosition;
                    moveData.forwardDirection = spawnBoltData.spawnDirection;

                    EntityManager.SetComponentData<BoltMoveData>(newSpawnedBoltEntityArray[i], moveData);
                }

                newSpawnedBoltEntityArray.Dispose();
            }
            else
            {
                //For AI bolts, create a job to set the boltMoveData of the new entity
                //Use GetComponentDataFromEntity the get the components we need
                SetAIBoltMoveDataJob setAiBoldMoveDataJob = new SetAIBoltMoveDataJob
                {
                    spawningFromEntityList = entityList,
                    spawnedBoltEntityArray = newSpawnedBoltEntityArray,
                    aiSpawnBoltDataFromEntity = GetComponentDataFromEntity<AISpawnBoltData>(),
                    boldMoveDataFromEntity = GetComponentDataFromEntity<BoltMoveData>(),
                };

                jobDepencyToReturn = setAiBoldMoveDataJob.Schedule(newSpawnedBoltEntityArray.Length,
                                                                   MonoBehaviourECSBridge.Instance.GetJobBatchCount(newSpawnedBoltEntityArray.Length),
                                                                   jobDepency);
            }


            UnityEngine.Profiling.Profiler.EndSample();

            return jobDepencyToReturn;
        }


        void MoveEntityinQueueToList(NativeQueue<Entity> entityQueue, NativeList<Entity> boltSpawnListToUse)
        {
            int entityQueueSize = entityQueue.Count;

            if (entityQueueSize == 0)
            {
                return;
            }

            UnityEngine.Profiling.Profiler.BeginSample("SpawnBoltFromEntityinQueue");

            //Resize our list if needed
            if (entityQueueSize > boltSpawnListToUse.Capacity)
            {
                boltSpawnListToUse.Capacity *= 2;
            }

            //Add entities to our list if they still exist
            //The DestroySystem might have destroyed this entity before this system
            while (entityQueue.Count > 0)
            {
                Entity entityToSpawnFrom = entityQueue.Dequeue();
                if (!EntityManager.Exists(entityToSpawnFrom))
                {
                    continue;
                }

                boltSpawnListToUse.Add(entityToSpawnFrom);
            }


            UnityEngine.Profiling.Profiler.EndSample();
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            playerBoltSpawnList.Clear();
            aiBoltSpawnList.Clear();

            //Move our entities from a queue to a list after testing if they still exist
            MoveEntityinQueueToList(aiBoltSpawnQueue, aiBoltSpawnList);
            MoveEntityinQueueToList(playerBoltSpawnQueue, playerBoltSpawnList);

            //Spawn the bolts from the lists, return a jobHandle (if no job are spawned, return the dependecy passed in parameter)
            JobHandle spawnBoltJobHandle;
            spawnBoltJobHandle = SpawnBoltFromEntityList(playerBoltSpawnList, prefabPlayerBolt, true, inputDeps);
            spawnBoltJobHandle = SpawnBoltFromEntityList(aiBoltSpawnList, prefabEnemyBolt, false, spawnBoltJobHandle);

            return spawnBoltJobHandle;
        }
    }

}


