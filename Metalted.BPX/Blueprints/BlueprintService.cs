﻿using FluentResults;
using Metalted.BPX.Blueprints.Resources;
using Metalted.BPX.Data.Entities;
using Metalted.BPX.Storage;
using Metalted.BPX.Users;
using Microsoft.EntityFrameworkCore;

namespace Metalted.BPX.Blueprints;

public interface IBlueprintService
{
    Result<bool> Exists(ulong steamId, string name);
    IEnumerable<Blueprint> Latest(int amount);
    Task<Result<Blueprint>> Submit(ulong steamId, BlueprintResource resource);
    IEnumerable<Blueprint> Search(SearchResource resource);
}

public class BlueprintService : IBlueprintService
{
    private readonly IBlueprintRepository _repository;
    private readonly IStorageService _storageService;
    private readonly IUserService _userService;

    public BlueprintService(
        IBlueprintRepository repository,
        IStorageService storageService,
        IUserService userService)
    {
        _repository = repository;
        _storageService = storageService;
        _userService = userService;
    }

    public Result<bool> Exists(ulong steamId, string name)
    {
        if (!_userService.TryGet(steamId, out User? user))
        {
            return Result.Fail("User not found");
        }

        if (user.Banned)
        {
            return Result.Fail("User is banned");
        }

        return _repository.GetSingle(x => x.Name == name && x.IdUser == user.Id) != null;
    }

    public IEnumerable<Blueprint> Latest(int amount)
    {
        return _repository.GetAll(set => set.Include(x => x.User))
            .OrderByDescending(x => x.DateUpdated ?? x.DateCreated)
            .Take(amount);
    }

    public async Task<Result<Blueprint>> Submit(ulong steamId, BlueprintResource resource)
    {
        if (!_userService.TryGet(steamId, out User? user))
        {
            return Result.Fail("User not found");
        }

        if (user.Banned)
        {
            return Result.Fail("User is banned");
        }

        Blueprint? existing = _repository.GetSingle(x => x.Name == resource.Name && x.IdUser == user.Id);

        if (existing != null)
        {
            Result saveResult = await UploadData(user, resource, existing.FileId);
            if (saveResult.IsFailed)
            {
                return saveResult;
            }

            if (resource.Tags != null && resource.Tags.Count > 0)
            {
                existing.Tags = resource.Tags;
            }

            existing = _repository.Update(existing);
            return Result.Ok(existing);
        }
        else
        {
            string fileGuid = Guid.NewGuid().ToString();
            Result saveResult = await UploadData(user, resource, fileGuid);
            if (saveResult.IsFailed)
            {
                return saveResult;
            }

            Blueprint blueprint = _repository.Insert(
                new Blueprint
                {
                    IdUser = user.Id,
                    Name = resource.Name,
                    FileId = fileGuid,
                    Tags = resource.Tags
                });

            return Result.Ok(blueprint);
        }
    }

    private async Task<Result> UploadData(User user, BlueprintResource resource, string fileGuid)
    {
        Result saveResult = await _storageService.SaveBlueprint(
            user.Id,
            fileGuid,
            Convert.FromBase64String(resource.BlueprintBase64));

        if (saveResult.IsFailed)
        {
            return saveResult;
        }

        Result saveImageResult = await _storageService.SaveImage(
            user.Id,
            fileGuid,
            Convert.FromBase64String(resource.ImageBase64));

        if (saveImageResult.IsFailed)
        {
            return saveImageResult;
        }

        return Result.Ok();
    }


    public IEnumerable<Blueprint> Search(SearchResource resource)
    {
        List<Blueprint> blueprints = _repository.GetAll(set => set.Include(x => x.User)).ToList();
        List<Blueprint> filtered = new();

        foreach (Blueprint blueprint in blueprints)
        {
            bool hasCreator = !string.IsNullOrWhiteSpace(resource.Creator);
            bool hasTags = resource.Tags != null && resource.Tags.Length > 0;
            bool hasTerms = resource.Terms != null && resource.Terms.Length > 0;

            if (hasCreator && !MatchesCreator(blueprint, resource.Creator))
                continue;

            if (hasTags && !MatchesAllTags(blueprint, resource.Tags))
                continue;

            // Use the terms to look through both the tags and the name
            if (hasTerms && !MatchesAllTerms(blueprint, resource.Terms) && !MatchesAllTags(blueprint, resource.Terms))
                continue;

            filtered.Add(blueprint);
        }

        return filtered;
    }

    private static bool MatchesCreator(Blueprint blueprint, string creator)
    {
        return blueprint.User.SteamName.Contains(creator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAllTerms(Blueprint blueprint, string[] terms)
    {
        if (terms.Length == 0)
            return false;

        foreach (string term in terms)
        {
            if (!blueprint.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool MatchesAllTags(Blueprint blueprint, string[] tags)
    {
        if (tags.Length == 0)
            return false;

        foreach (string tag in tags)
        {
            if (!blueprint.Tags.Any(x => x.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }
}
