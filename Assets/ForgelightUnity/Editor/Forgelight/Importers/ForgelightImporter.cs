﻿namespace ForgelightUnity.Editor.Forgelight.Importers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using Assets.Pack;
    using UnityEditor;
    using Utils;
    using MathUtils = Utils.MathUtils;
    using Parallel = Utils.Parallel;

    public abstract class ForgelightImporter<T1,T2> where T1 : IPoolable, new()
                                                    where T2 : new()
    {
        protected abstract string ProgressItemPrefix { get; }
        protected abstract AssetType AssetType { get; }

        protected ForgelightGame ForgelightGame { get; private set; }
        protected string ResourceDir { get; private set; }

        // Jobs
        private ConcurrentQueue<AssetRef> assetsToProcess;

        protected int AssetsProcessed;
        protected string LastAssetProcessed;

        public ObjectPool<T1> ObjectPool;

        public ForgelightImporter()
        {
            ObjectPool = new ObjectPool<T1>();
        }

        public ForgelightImporter(int objectPoolSize)
        {
            ObjectPool = new ObjectPool<T1>(objectPoolSize, objectPoolSize);
        }

        public bool RunImport(ForgelightGame forgelightGame, float progress0, float progress100)
        {
            ForgelightGame = forgelightGame;
            ResourceDir = forgelightGame.GameInfo.FullResourceDirectory;

            // Progress Bar
            AssetsProcessed = 0;
            LastAssetProcessed = "";

            // Operation Items
            object oLock = new object();
            assetsToProcess = new ConcurrentQueue<AssetRef>(forgelightGame.AssetsByType[AssetType]);
            int totalAssetCount = assetsToProcess.Count;
            Parallel.AsyncForEach<bool> parallelTask = System.Threading.Tasks.Parallel.ForEach;
            bool cancel = false;

            IAsyncResult result = parallelTask.BeginInvoke(WorkComplete(), job =>
            {
                // Setup the Thread
                T2 threadData = new T2();

                // Process all available jobs.
                while (!cancel && JobsAvailable())
                {
                    AssetRef currentJob;
                    assetsToProcess.TryDequeue(out currentJob);

                    if (currentJob == null)
                    {
                        continue;
                    }

                    Import(currentJob, threadData, oLock);
                    Interlocked.Increment(ref AssetsProcessed);
                    LastAssetProcessed = currentJob.Name;
                }
            }, null, null);

            while (!RunBackgroundTasks() || !result.IsCompleted)
            {
                cancel = EditorUtility.DisplayCancelableProgressBar("Forgelight - " + forgelightGame.GameInfo.Name, ProgressItemPrefix + LastAssetProcessed, MathUtils.Remap01(AssetsProcessed / (float) totalAssetCount, progress0, progress100));
            }

            parallelTask.EndInvoke(result);

            if (cancel)
            {
                EditorUtility.ClearProgressBar();
            }
            return !cancel;
        }

        protected virtual bool RunBackgroundTasks()
        {
            return true;
        }

        /// <summary>
        /// Ensures threads remaining running while jobs are available.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<bool> WorkComplete()
        {
            bool jobsAvailable = JobsAvailable();

            if (jobsAvailable)
            {
                yield return false;
            }
        }

        protected virtual bool JobsAvailable()
        {
            if (!assetsToProcess.IsEmpty)
            {
                return true;
            }

            return false;
        }

        protected abstract void Import(AssetRef asset, T2 data, object oLock);
    }
}