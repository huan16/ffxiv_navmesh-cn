﻿using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public class NavmeshManager : IDisposable
{
    public bool AutoLoad = true; // whether we load/build mesh automatically when changing zone
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;
    public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;
    public Navmesh? Navmesh => _navmesh;
    public NavmeshQuery? Query => _query;
    public float LoadTaskProgress => _loadTask != null ? _loadTaskProgress : -1; // returns negative value if task is not running
    public string CurrentKey => _lastKey;
    public bool PathfindInProgress => _currentPathfindTask != null;
    public int NumQueuedPathfindRequests => _queuedPathfindTasks.Count;

    private DirectoryInfo _cacheDir;
    private string _lastKey = "";
    private Task<Navmesh>? _loadTask;
    private volatile float _loadTaskProgress;
    private Navmesh? _navmesh;
    private NavmeshQuery? _query;
    private CancellationTokenSource? _queryCancelSource;
    private List<Task<List<Vector3>>> _queuedPathfindTasks = new(); // will be executed one by one in order after current task completes
    private Task<List<Vector3>>? _currentPathfindTask;

    public NavmeshManager(DirectoryInfo cacheDir)
    {
        _cacheDir = cacheDir;
        cacheDir.Create(); // ensure directory exists
    }

    public void Dispose()
    {
        if (_loadTask != null)
        {
            if (!_loadTask.IsCompleted)
                _loadTask.Wait();
            _loadTask.Dispose();
            _loadTask = null;
        }
        ClearState();
    }

    public void Update()
    {
        if (_loadTask != null)
        {
            if (!_loadTask.IsCompleted)
                return; // async mesh load task is still in progress, do nothing; note that we don't want to start multiple concurrent tasks on rapid transitions

            Service.Log.Information($"完成向 '{_lastKey}' 的转换");
            try
            {
                _navmesh = _loadTask.Result;
                _query = new(_navmesh);
                _queryCancelSource = new();
                OnNavmeshChanged?.Invoke(_navmesh, _query);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"构建失败: {ex}");
            }
            _loadTask.Dispose();
            _loadTask = null;
        }

        var curKey = GetCurrentKey();
        if (curKey != _lastKey)
        {
            // navmesh needs to be reloaded
            if (!AutoLoad)
            {
                if (_lastKey.Length == 0)
                    return; // nothing is loaded, and auto-load is forbidden
                curKey = ""; // just unload existing mesh
            }

            Service.Log.Info($"开始从 '{_lastKey}' 转换至 '{curKey}'");
            _lastKey = curKey;
            Reload(true);
            return; // mesh load is now in progress
        }

        // at this point, we're not loading a mesh
        if (_query != null)
        {
            if (_currentPathfindTask != null && (_currentPathfindTask.IsCompleted || _currentPathfindTask.IsCanceled))
            {
                _currentPathfindTask = null;
            }

            if (_currentPathfindTask == null && _queuedPathfindTasks.Count > 0)
            {
                // kick off new pathfind task
                _currentPathfindTask = _queuedPathfindTasks[0];
                _queuedPathfindTasks.RemoveAt(0);
                _currentPathfindTask.Start();
            }
        }
    }

    public bool Reload(bool allowLoadFromCache)
    {
        if (_loadTask != null)
        {
            Service.Log.Error("无法开始重加载 —— 仍有另一项任务正在进行中");
            return false; // some task is already in progress...
        }

        ClearState();
        if (_lastKey.Length > 0)
        {
            var scene = new SceneDefinition();
            scene.FillFromActiveLayout();
            var cacheKey = GetCacheKey(scene);
            _loadTaskProgress = 0;
            _loadTask = Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache));
        }
        return true;
    }

    public Task<List<Vector3>>? QueryPath(Vector3 from, Vector3 to, bool flying, CancellationToken externalCancel = default)
    {
        var query = _query;
        if (_queryCancelSource == null || query == null)
        {
            Service.Log.Error($"无法查询, 数据构建未完成");
            return null;
        }

        // task can be cancelled either by internal request (i.e. when navmesh is reloaded) or external
        var combined = CancellationTokenSource.CreateLinkedTokenSource(_queryCancelSource.Token, externalCancel);
        var task = new Task<List<Vector3>>(() =>
        {
            using var autoDisposeCombined = combined;
            combined.Token.ThrowIfCancellationRequested();
            return flying ? query.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token) : query.PathfindMesh(from, to, UseRaycasts, UseStringPulling, combined.Token);
        });
        _queuedPathfindTasks.Add(task);
        return task;
    }

    public void CancelAllQueries()
    {
        if (_queryCancelSource == null)
        {
            Service.Log.Error($"无法查询, 数据未加载");
            return;
        }

        _queryCancelSource.Cancel(); // this will cancel current and all queued pathfind tasks
        _queryCancelSource.Dispose();
        _queryCancelSource = new(); // create new token source for future tasks
    }

    // if non-empty string is returned, active layout is ready
    private unsafe string GetCurrentKey()
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return ""; // layout not ready

        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        var terrRow = Service.LuminaRow<Lumina.Excel.GeneratedSheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);
        return $"{terrRow?.Bg}//{filterKey:X}//{layout->ActiveFestivals[0]:X}.{layout->ActiveFestivals[1]:X}.{layout->ActiveFestivals[2]:X}.{layout->ActiveFestivals[3]:X}";
    }

    private unsafe string GetCacheKey(SceneDefinition scene)
    {
        // note: festivals are active globally, but majority of zones don't have festival-specific layers, so we only want real ones in the cache key
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var filter = LayoutUtils.FindFilter(layout);
        var filterKey = filter != null ? filter->Key : 0;
        var terrRow = Service.LuminaRow<Lumina.Excel.GeneratedSheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);
        return $"{terrRow?.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{string.Join('.', scene.FestivalLayers.Select(id => id.ToString("X")))}";
    }

    private void ClearState()
    {
        _queryCancelSource?.Cancel();
        _queryCancelSource?.Dispose();
        _queryCancelSource = null;
        _queuedPathfindTasks.Clear();
        _currentPathfindTask = null;

        OnNavmeshChanged?.Invoke(null, null);
        _query = null;
        _navmesh = null;
    }

    private Navmesh BuildNavmesh(SceneDefinition scene, string cacheKey, bool allowLoadFromCache)
    {
        var customization = NavmeshCustomizationRegistry.ForTerritory(scene.TerritoryID);

        // try reading from cache
        var cache = new FileInfo($"{_cacheDir.FullName}/{cacheKey}.navmesh");
        if (allowLoadFromCache && cache.Exists)
        {
            try
            {
                Service.Log.Debug($"加载缓存: {cache.FullName}");
                using var stream = cache.OpenRead();
                using var reader = new BinaryReader(stream);
                return Navmesh.Deserialize(reader, customization.Version);
            }
            catch (Exception ex)
            {
                Service.Log.Debug($"加载缓存失败: {ex}");
            }
        }

        // cache doesn't exist or can't be used for whatever reason - build navmesh from scratch
        // TODO: we can build multiple tiles concurrently
        var builder = new NavmeshBuilder(scene, customization);
        var deltaProgress = 1.0f / (builder.NumTilesX * builder.NumTilesZ);
        for (int z = 0; z < builder.NumTilesZ; ++z)
        {
            for (int x = 0; x < builder.NumTilesX; ++x)
            {
                builder.BuildTile(x, z);
                _loadTaskProgress += deltaProgress;
            }
        }

        // write results to cache
        {
            Service.Log.Debug($"写入缓存: {cache.FullName}");
            using var stream = cache.OpenWrite();
            using var writer = new BinaryWriter(stream);
            builder.Navmesh.Serialize(writer);
        }
        return builder.Navmesh;
    }
}
