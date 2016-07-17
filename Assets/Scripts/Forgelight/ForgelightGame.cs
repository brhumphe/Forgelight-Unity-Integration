﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Forgelight.Formats.Adr;
using Forgelight.Formats.Areas;
using Forgelight.Formats.Cnk;
using Forgelight.Formats.Dma;
using Forgelight.Formats.Dme;
using Forgelight.Formats.Zone;
using Forgelight.Pack;
using Forgelight.Utils;
using UnityEditor;
using Debug = UnityEngine.Debug;
using MathUtils = Forgelight.Utils.MathUtils;

namespace Forgelight
{
    public class ForgelightGame
    {
        //Info
        public string Name { get; private set; }
        public string PackDirectory { get; private set; }
        public string ResourceDirectory { get; private set; }

        //Available Assets
        public SortedDictionary<string, Adr> AvailableActors { get; private set; }
        public SortedDictionary<string, Zone> AvailableZones { get; private set; }
        public SortedDictionary<string, Areas> AvailableAreaDefinitions { get; private set; }

        //Data
        public List<Pack.Pack> Packs { get; private set; }
        public ConcurrentDictionary<Asset.Types, List<Asset>> AssetsByType { get; private set; }
        public MaterialDefinitionManager MaterialDefinitionManager { get; private set; }

        // Internal cache to check whether a pack has already been loaded
        private ConcurrentDictionary<string, Pack.Pack> packLookupCache = new ConcurrentDictionary<string, Pack.Pack>();

        //Progress
        private float lastProgress;

        public ForgelightGame(string name, string packDirectory, string resourceDirectory)
        {
            Name = name;
            PackDirectory = packDirectory;
            ResourceDirectory = resourceDirectory;

            Packs = new List<Pack.Pack>();
            AssetsByType = new ConcurrentDictionary<Asset.Types, List<Asset>>();

            foreach (Enum type in Enum.GetValues(typeof(Asset.Types)))
            {
                AssetsByType.TryAdd((Asset.Types) type, new List<Asset>());
            }
        }

        public void LoadPack(string path)
        {
            Pack.Pack pack;

            if (!packLookupCache.TryGetValue(path, out pack))
            {
                pack = Pack.Pack.LoadBinary(path);

                if (pack != null)
                {
                    packLookupCache.TryAdd(path, pack);
                    Packs.Add(pack);

                    foreach (Asset asset in pack.Assets)
                    {
                        AssetsByType[asset.Type].Add(asset);
                    }
                }
            }
        }

        public bool LoadZoneFromFile(string path)
        {
            try
            {
                using (FileStream fileStream = File.OpenRead(path))
                {
                    string zoneName = Path.GetFileNameWithoutExtension(path);
                    Zone zone = Zone.LoadFromStream(Path.GetFileName(path), fileStream);

                    if (zone != null)
                    {
                        zoneName = zoneName + " (" + path + ")";
                        AvailableZones[zoneName] = zone;

                        if (DialogUtils.DisplayCancelableDialog("Change Zones", "Would you like to load this zone now?"))
                        {
                            ForgelightExtension.Instance.ZoneManager.ChangeZone(ForgelightExtension.Instance.ForgelightGameFactory.ActiveForgelightGame, zone);
                        }

                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("An error occurred while importing zone at: " + path + ". " + e.Message);
            }

            return false;
        }

        public MemoryStream CreateAssetMemoryStreamByName(string name)
        {
            MemoryStream memoryStream = null;

            if (name == null)
            {
                Debug.LogError("Asset Name is null");
            }



            foreach (Pack.Pack pack in Packs)
            {
                if (pack == null)
                {
                    Debug.LogError("Pack is null");
                }

                memoryStream = pack.CreateAssetMemoryStreamByName(name);

                if (memoryStream != null)
                {
                    break;
                }
            }

            return memoryStream;
        }

        private void ProgressBar(float progress, string currentTask)
        {
            if (progress == lastProgress)
            {
                return;
            }

            EditorUtility.DisplayProgressBar("Forgelight - " + Name, currentTask, progress);
            lastProgress = progress;
        }

        public void OnLoadComplete()
        {
            EditorUtility.ClearProgressBar();
        }

        public void LoadPackFiles(float progress0, float progress100)
        {
            string[] files = Directory.GetFiles(PackDirectory, "*.pack");

            //Load Pack files into AssetManager.
            ProgressBar(progress0, "Loading Pack Data...");

            int packsProcessed = 0;
            string assetProcessing = "";

            Parallel.AsyncForEach<string> parallelTask = System.Threading.Tasks.Parallel.ForEach;

            IAsyncResult result = parallelTask.BeginInvoke(files, file =>
            {
                assetProcessing = Path.GetFileName(file);

                LoadPack(file);
                Interlocked.Increment(ref packsProcessed);
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(packsProcessed / (float)files.Length, progress0, progress100), "Loading Pack File: " + assetProcessing);
            }

            parallelTask.EndInvoke(result);
        }

        public void InitializeMaterialDefinitionManager()
        {
            MaterialDefinitionManager = new MaterialDefinitionManager(this);
        }

        public void UpdateActors(float progress0, float progress100)
        {
            ProgressBar(progress0, "Updating Actors List...");

            List<Asset> actors = AssetsByType[Asset.Types.ADR];
            AvailableActors = new SortedDictionary<string, Adr>();

            int assetsProcessed = 0;
            string lastAssetProcessed = "";
            object listLock = new object();

            Parallel.AsyncForEach<Asset> parallelTask = System.Threading.Tasks.Parallel.ForEach;

            IAsyncResult result = parallelTask.BeginInvoke(actors, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                string adrName = Path.GetFileNameWithoutExtension(asset.Name);

                MemoryStream memoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name);
                Adr adr = Adr.LoadFromStream(adrName, memoryStream);

                Interlocked.Increment(ref assetsProcessed);

                if (adr == null)
                {
                    return;
                }

                lock (listLock)
                {
                    lastAssetProcessed = adrName;
                    AvailableActors.Add(asset.Name, adr);
                }
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(assetsProcessed / (float)actors.Count, progress0, progress100), "Updating Actors List: " + lastAssetProcessed);
            }

            parallelTask.EndInvoke(result);
        }

        public void UpdateZones(float progress0, float progress100)
        {
            ProgressBar(progress0, "Updating Zones...");

            List<Asset> zones = AssetsByType[Asset.Types.ZONE];
            AvailableZones = new SortedDictionary<string, Zone>();

            int assetsProcessed = 0;
            string lastAssetProcessed = "";
            object listLock = new object();

            Parallel.AsyncForEach<Asset> parallelTask = System.Threading.Tasks.Parallel.ForEach;

            IAsyncResult result = parallelTask.BeginInvoke(zones, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                string zoneName = Path.GetFileNameWithoutExtension(asset.Name);

                MemoryStream memoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name);
                Zone zone = Zone.LoadFromStream(asset.Name, memoryStream);

                Interlocked.Increment(ref assetsProcessed);

                if (zone == null)
                {
                    return;
                }

                lock (listLock)
                {
                    lastAssetProcessed = zoneName;
                    zoneName = zoneName + " (" + asset.Pack.Name + ")";
                    AvailableZones[zoneName] = zone;
                }
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(assetsProcessed / (float)zones.Count, progress0, progress100), "Updating Zone: " + lastAssetProcessed);
            }

            parallelTask.EndInvoke(result);
        }

        public void UpdateAreas(float progress0, float progress100)
        {
            ProgressBar(progress0, "Loading Area Definitions");

            List<Asset> xmlFiles = AssetsByType[Asset.Types.XML];

            AvailableAreaDefinitions = new SortedDictionary<string, Areas>();

            int assetsProcessed = 0;
            string lastAssetProcessed = "";
            object listLock = new object();

            Parallel.AsyncForEach<Asset> parallelTask = System.Threading.Tasks.Parallel.ForEach;

            IAsyncResult result = parallelTask.BeginInvoke(xmlFiles, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                string areaDefinitionsXML = Path.GetFileNameWithoutExtension(asset.Name);

                if (!areaDefinitionsXML.EndsWith("Areas"))
                {
                    return;
                }

                MemoryStream memoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name);
                Areas areas = Areas.LoadFromStream(asset.Name, memoryStream);

                Interlocked.Increment(ref assetsProcessed);

                if (areas == null)
                {
                    return;
                }

                lock (listLock)
                {
                    lastAssetProcessed = areaDefinitionsXML;
                    areaDefinitionsXML = areaDefinitionsXML + " (" + asset.Pack.Name + ")";
                    AvailableAreaDefinitions[areaDefinitionsXML] = areas;
                }
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(assetsProcessed / (float)xmlFiles.Count, progress0, progress100), "Loading Area Definitions: " + lastAssetProcessed);
            }

            parallelTask.EndInvoke(result);
        }

        public void ExportModels(float progress0, float progress100)
        {
            ProgressBar(progress0, "Exporting Models...");

            List<Asset> modelAssets = AssetsByType[Asset.Types.DME];
            int assetsProcessed = 0;
            string lastAssetProcessed = "";

            Parallel.AsyncForEach<Asset> parallelTask = System.Threading.Tasks.Parallel.ForEach;

            IAsyncResult result = parallelTask.BeginInvoke(modelAssets, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                //Don't export if the file already exists.
                if (!File.Exists(ResourceDirectory + "/Models/" + Path.GetFileNameWithoutExtension(asset.Name) + ".obj"))
                {
                    lastAssetProcessed = asset.Name;

                    using (MemoryStream modelMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
                    {
                        Model model = Model.LoadFromStream(asset.Name, modelMemoryStream);

                        if (model != null)
                        {
                            ModelExporter.ExportModel(this, model, ResourceDirectory + "/Models");
                        }
                    }
                }

                Interlocked.Increment(ref assetsProcessed);
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(assetsProcessed / (float)modelAssets.Count, progress0, progress100), "Exporting Model: " + lastAssetProcessed);
            }

            parallelTask.EndInvoke(result);
        }

        //TODO Less Code Duplication.
        //TODO Update CNK0 Parsing. The current format seems to be incorrect.
        public void ExportTerrain(float progress0, float progress100)
        {
            int chunksProcessed = 0;
            int texturesProcessed = 0;
            string assetProcessing = "";

            //CNK0 (Geo)
            List<Asset> terrainAssetsCnk0 = AssetsByType[Asset.Types.CNK0];

            Parallel.AsyncForEach<Asset> parallelTask = System.Threading.Tasks.Parallel.ForEach;
            IAsyncResult result = parallelTask.BeginInvoke(terrainAssetsCnk0, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                using (MemoryStream terrainMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
                {
                    assetProcessing = asset.Name;

                    //Cnk0 chunk = Cnk0.LoadFromStream(asset.Name, terrainMemoryStream);

                    //if (chunk != null)
                    //{
                    //    ChunkExporter.ExportChunk(this, chunk, ResourceDirectory + "/Terrain");
                    //}

                    Interlocked.Increment(ref chunksProcessed);
                }
            }, null, null);

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01(chunksProcessed / ((float)terrainAssetsCnk0.Count), progress0, progress100), "Exporting Chunk: " + assetProcessing);
            }

            chunksProcessed = 0;
            texturesProcessed = 0;

            //CNK1 (Geo)
            List<Asset> terrainAssetsCnk1 = AssetsByType[Asset.Types.CNK1];

            parallelTask = System.Threading.Tasks.Parallel.ForEach;

            result = parallelTask.BeginInvoke(terrainAssetsCnk1, asset =>
            {
                if (asset == null)
                {
                    return;
                }

                using (MemoryStream terrainMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
                {
                    assetProcessing = asset.Name;

                    CnkLOD chunk = CnkLOD.LoadFromStream(asset.Name, terrainMemoryStream);

                    if (chunk != null)
                    {
                        ChunkExporter.ExportChunk(this, chunk, ResourceDirectory + "/Terrain");
                    }

                    Interlocked.Increment(ref chunksProcessed);
                }
            }, null, null);

            //CNK1 (Textures)
            foreach (Asset asset in terrainAssetsCnk1)
            {
                using (MemoryStream terrainMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
                {
                    CnkLOD chunk = CnkLOD.LoadFromStream(asset.Name, terrainMemoryStream);

                    if (chunk != null)
                    {
                        ChunkExporter.ExportTextures(this, chunk, ResourceDirectory + "/Terrain");
                    }

                    texturesProcessed++;

                    assetProcessing = asset.Name;
                    ProgressBar(MathUtils.Remap01((texturesProcessed + chunksProcessed) / ((float)terrainAssetsCnk1.Count * 2), progress0, progress100), "Exporting Chunk: " + assetProcessing);
                }
            }

            while (!result.IsCompleted)
            {
                ProgressBar(MathUtils.Remap01((texturesProcessed + chunksProcessed) / ((float)terrainAssetsCnk1.Count * 2), progress0, progress100), "Exporting Chunk: " + assetProcessing);
            }

            parallelTask.EndInvoke(result);

            ////CNK2
            //ProgressBar(progress0 + MathUtils.RemapProgress(0.50f, progress0, progress100), "Exporting Terrain Data (LOD 2)...");
            //List<Asset> terrainAssetsCnk2 = AssetsByType[Asset.Types.CNK2];
            //int terrainAssetsCnk2Processed = 0;

            //Parallel.ForEach(terrainAssetsCnk2, asset =>
            //{
            //    using (MemoryStream terrainMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
            //    {
            //        CnkLOD chunk = CnkLOD.LoadFromStream(asset.Name, terrainMemoryStream);
            //    }

            //    Interlocked.Increment(ref terrainAssetsCnk2Processed);
            //    //ProgressBar(MathUtils.RemapProgress((float)terrainAssetsCnk2Processed / (float)terrainAssetsCnk2.Count, progress0, progress100), "Exporting Chunk (LOD2): " + Path.GetFileName(asset.Name));
            //});

            ////CNK3
            //ProgressBar(progress0 + MathUtils.RemapProgress(0.75f, progress0, progress100), "Exporting Terrain Data (LOD 3)...");
            //List<Asset> terrainAssetsCnk3 = AssetsByType[Asset.Types.CNK3];
            //int terrainAssetsCnk3Processed = 0;

            //Parallel.ForEach(terrainAssetsCnk3, asset =>
            //{
            //    using (MemoryStream terrainMemoryStream = asset.Pack.CreateAssetMemoryStreamByName(asset.Name))
            //    {
            //        CnkLOD chunk = CnkLOD.LoadFromStream(asset.Name, terrainMemoryStream);
            //    }

            //    Interlocked.Increment(ref terrainAssetsCnk3Processed);
            //    //ProgressBar(MathUtils.RemapProgress((float)terrainAssetsCnk3Processed / (float)terrainAssetsCnk3.Count, progress0, progress100), "Exporting Chunk (LOD3): " + Path.GetFileName(asset.Name));
            //});
        }
    }
}
