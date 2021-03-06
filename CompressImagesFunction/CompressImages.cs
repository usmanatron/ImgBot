﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Common;
using ImageMagick;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CompressImagesFunction
{
    public static class CompressImages
    {
        private static string azGitPath = "D:\\Program Files\\Git\\bin\\git.exe";

        public static bool Run(CompressimagesParameters parameters, ILogger logger)
        {
            var gitPath = azGitPath;
            if (File.Exists(gitPath) == false)
            {
                gitPath = "git"; // rely on $PATH
            }

            CredentialsHandler credentialsProvider =
                (url, user, cred) =>
                new UsernamePasswordCredentials { Username = KnownGitHubs.Username, Password = parameters.Password };

            // clone
            var authCloneUrl = parameters.CloneUrl
                .Replace("https://github.com", $"https://{KnownGitHubs.Username}:{parameters.Password}@github.com");
            var cloneArgs = new[]
            {
                "clone",
                "--depth 1",
                authCloneUrl,
                parameters.LocalPath
            };
            var gitClone = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gitPath,
                    Arguments = string.Join(" ", cloneArgs),
                }
            };
            gitClone.Start();
            gitClone.WaitForExit();
            var repo = new Repository(parameters.LocalPath);
            var remote = repo.Network.Remotes["origin"];

            // check if we have the branch already or this is empty repo
            try
            {
                if (repo.Network.ListReferences(remote, credentialsProvider).Any() == false)
                {
                    logger.LogInformation("CompressImagesFunction: no references found for {Owner}/{RepoName}", parameters.RepoOwner, parameters.RepoName);
                    return false;
                }

                if (repo.Network.ListReferences(remote, credentialsProvider).Any(x => x.CanonicalName == $"refs/heads/{KnownGitHubs.BranchName}"))
                {
                    logger.LogInformation("CompressImagesFunction: branch already exists for {Owner}/{RepoName}", parameters.RepoOwner, parameters.RepoName);
                    return false;
                }
            }
            catch (Exception e)
            {
                // log + ignore
                logger.LogWarning(e, "CompressImagesFunction: issue checking for existing branch or empty repo for {Owner}/{RepoName}", parameters.RepoOwner, parameters.RepoName);
            }

            var repoConfiguration = new RepoConfiguration();

            try
            {
                // see if .imgbotconfig exists in repo root
                var repoConfigJson = File.ReadAllText(parameters.LocalPath + Path.DirectorySeparatorChar + ".imgbotconfig");
                if (!string.IsNullOrEmpty(repoConfigJson))
                {
                    repoConfiguration = JsonConvert.DeserializeObject<RepoConfiguration>(repoConfigJson);
                }
            }
            catch
            {
                // ignore
            }

            if (Schedule.ShouldOptimizeImages(repoConfiguration, repo) == false)
            {
                logger.LogInformation("CompressImagesFunction: skipping optimization due to schedule for {Owner}/{RepoName}", parameters.RepoOwner, parameters.RepoName);
                return false;
            }

            // save a pointer to the tip
            var tipSha = repo.Head.Tip.Sha;

            // check out the branch
            repo.CreateBranch(KnownGitHubs.BranchName);
            var branch = Commands.Checkout(repo, KnownGitHubs.BranchName);

            // reset any mean files
            repo.Reset(ResetMode.Mixed, repo.Head.Tip);

            // optimize images
            var imagePaths = ImageQuery.FindImages(parameters.LocalPath, repoConfiguration);
            var optimizedImages = OptimizeImages(repo, parameters.LocalPath, imagePaths, logger, repoConfiguration.AggressiveCompression);
            if (optimizedImages.Length == 0)
            {
                return false;
            }

            foreach (var image in optimizedImages)
            {
                Commands.Stage(repo, image.OriginalPath);
            }

            // create commit message based on optimizations
            var commitMessage = CommitMessage.Create(optimizedImages);

            // commit
            var signature = new Signature(KnownGitHubs.ImgBotLogin, KnownGitHubs.ImgBotEmail, DateTimeOffset.Now);
            repo.Commit(commitMessage, signature, signature);

            // We just made a normal commit, now we are going to capture all the values generated from that commit
            // then rewind and make a signed commit
            var commitBuffer = Commit.CreateBuffer(
                repo.Head.Tip.Author,
                repo.Head.Tip.Committer,
                repo.Head.Tip.Message,
                repo.Head.Tip.Tree,
                repo.Head.Tip.Parents,
                true,
                null);

            var signedCommitData = CommitSignature.Sign(commitBuffer + "\n", parameters.PgpPrivateKeyStream, parameters.PgPPassword);

            repo.Reset(ResetMode.Soft, tipSha);
            var commitToKeep = repo.ObjectDatabase.CreateCommitWithSignature(commitBuffer, signedCommitData);

            repo.Refs.UpdateTarget(repo.Refs.Head, commitToKeep);
            var branchAgain = Commands.Checkout(repo, KnownGitHubs.BranchName);
            repo.Reset(ResetMode.Hard, commitToKeep.Sha);

            // push to GitHub
            var pushArgs = new[]
            {
                "push",
                remote.Name,
                KnownGitHubs.BranchName,
            };
            var gitpush = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    WorkingDirectory = parameters.LocalPath,
                    FileName = gitPath,
                    Arguments = string.Join(" ", pushArgs),
                }
            };
            gitpush.Start();
            gitpush.WaitForExit();

            return true;
        }

        private static CompressionResult[] OptimizeImages(Repository repo, string localPath, string[] imagePaths, ILogger logger, bool aggressiveCompression)
        {
            var optimizedImages = new List<CompressionResult>();
            ImageOptimizer imageOptimizer = new ImageOptimizer
            {
                OptimalCompression = true,
                IgnoreUnsupportedFormats = true,
            };

            Parallel.ForEach(imagePaths, image =>
            {
                try
                {
                    Console.WriteLine(image);
                    FileInfo file = new FileInfo(image);
                    double before = file.Length;
                    if (aggressiveCompression ? imageOptimizer.Compress(file) : imageOptimizer.LosslessCompress(file))
                    {
                        optimizedImages.Add(new CompressionResult
                        {
                            Title = image.Substring(localPath.Length),
                            OriginalPath = image,
                            SizeBefore = before / 1024d,
                            SizeAfter = file.Length / 1024d,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    logger.LogError(ex, $"Compression issue with {image}");
                }
            });

            logger.LogInformation("Compressed {NumImages}", optimizedImages.Count);
            return optimizedImages.ToArray();
        }
    }
}
