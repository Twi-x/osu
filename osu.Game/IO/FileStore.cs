﻿// Copyright (c) 2007-2017 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using osu.Framework.Extensions;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Database;

namespace osu.Game.IO
{
    /// <summary>
    /// Handles the Store and retrieval of Files/FileSets to the database backing
    /// </summary>
    public class FileStore : DatabaseBackedStore
    {
        private const string prefix = "files";

        public readonly ResourceStore<byte[]> Store;

        public FileStore(OsuDbContext connection, Storage storage) : base(connection, storage)
        {
            Store = new NamespacedResourceStore<byte[]>(new StorageBackedResourceStore(storage), prefix);
        }

        protected override void Prepare(bool reset = false)
        {
            if (reset)
            {
                if (Storage.ExistsDirectory(prefix))
                    Storage.DeleteDirectory(prefix);

                Connection.Database.ExecuteSqlCommand("DELETE FROM FileInfo");
            }
        }

        protected override void StartupTasks()
        {
            base.StartupTasks();
            deletePending();
        }

        public FileInfo Add(Stream data, bool reference = true)
        {
            string hash = data.ComputeSHA2Hash();

            var existing = Connection.FileInfo.FirstOrDefault(f => f.Hash == hash);

            var info = existing ?? new FileInfo { Hash = hash };

            string path = Path.Combine(prefix, info.StoragePath);

            // we may be re-adding a file to fix missing store entries.
            if (!Storage.Exists(path))
            {
                data.Seek(0, SeekOrigin.Begin);

                using (var output = Storage.GetStream(path, FileAccess.Write))
                    data.CopyTo(output);

                data.Seek(0, SeekOrigin.Begin);
            }

            if (reference || existing == null)
                Reference(info);

            return info;
        }

        public void Reference(params FileInfo[] files)
        {
            foreach (var f in files.GroupBy(f => f.ID))
            {
                var refetch = Connection.Find<FileInfo>(f.First().ID) ?? f.First();
                refetch.ReferenceCount += f.Count();
                Connection.FileInfo.Update(refetch);
            }

            Connection.SaveChanges();
        }

        public void Dereference(params FileInfo[] files)
        {
            foreach (var f in files.GroupBy(f => f.ID))
            {
                var refetch = Connection.Find<FileInfo>(f.First().ID);
                refetch.ReferenceCount -= f.Count();
                Connection.Update(refetch);
            }

            Connection.SaveChanges();
        }

        private void deletePending()
        {
            foreach (var f in Connection.FileInfo.Where(f => f.ReferenceCount < 1))
            {
                try
                {
                    Storage.Delete(Path.Combine(prefix, f.StoragePath));
                    Connection.FileInfo.Remove(f);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $@"Could not delete beatmap {f}");
                }
            }

            Connection.SaveChanges();
        }
    }
}
