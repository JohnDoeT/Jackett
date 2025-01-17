﻿using Jackett.Common.Services.Interfaces;
using NLog;
using System;
//#if !NET461
//using Mono.Unix;
//#endif

namespace Jackett.Server.Services
{
    public class FilePermissionService : IFilePermissionService
    {
        private Logger logger;

        public FilePermissionService(Logger l)
        {
            logger = l;
        }

        public void MakeFileExecutable(string path)
        {
#if !NET461

            //Calling the file permission service to limit usage to netcoreapp. The Mono.Posix.NETStandard library causes issues outside of .NET Core
            //https://github.com/xamarin/XamarinComponents/issues/282

            //logger.Debug($"Attempting to give execute permission to: {path}");
            //try
            //{
            //    UnixFileInfo jackettUpdaterFI = new UnixFileInfo(path)
            //    {
            //        FileAccessPermissions = FileAccessPermissions.UserReadWriteExecute | FileAccessPermissions.GroupRead | FileAccessPermissions.OtherRead
            //    };
            //}
            //catch (Exception ex)
            //{
            //    logger.Error(ex);
            //}
#endif
        }
    }
}
