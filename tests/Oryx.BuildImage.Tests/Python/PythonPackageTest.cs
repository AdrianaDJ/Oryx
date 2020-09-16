﻿// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// --------------------------------------------------------------------------------------------

using Microsoft.Oryx.BuildScriptGenerator.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Oryx.BuildImage.Tests.Python
{
    /// <summary>
    /// Tests that the Python platform can build/create python eggs and/or wheels.
    /// </summary>
    public class PythonPackageTest : PythonSampleAppsTestBase
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public PythonPackageTest(ITestOutputHelper output) : base(output)
        {
        }

        public static IEnumerable<object[]> PythonPackageExamples => new object[][]
        {
            new object[] { "boto3", "1.14.44",
                "git://github.com/boto/boto3.git" },
            new object[] { "botocore", "1.17.44",
                "git://github.com/boto/botocore.git" },
            new object[] { "pyasn1", "0.4.8",
                "git://github.com/etingof/pyasn1.git", "v0.4.8"},
        };

        private readonly string[] IgnoredTarEntries = new[] { "tutorial.rst", "docs/tutorial.rst" };

        [Theory]
        [MemberData(nameof(PythonPackageExamples))]
        public void CanBuildPython3Packages(
            string pkgName,
            string pkgVersion,
            string gitRepoUrl,
            string pkgTag = null,
            string[] requiredOsPackages = null)
        {
            const string tarListCmd = "tar -tvf";
            const string pypiTarPath = "/tmp/pypi-pkg.tar.gz";
            const string tarListMarker = "---TAR---";

            // Arrange
            var pkgSrcDir = "/tmp/pkg/src";
            var pkgBuildOutputDir = "/tmp/pkg/out";
            var oryxPackTarOutput = $"{pkgBuildOutputDir}/dist/{pkgName}-{pkgVersion}.tar.gz";
            var oryxPackEggOutput = $"{pkgBuildOutputDir}/dist/{pkgName}-{pkgVersion}-py3.8.egg";
            var oryxPackWheelOutput = $"{pkgBuildOutputDir}/dist/{pkgName}-{pkgVersion}-py2.py3-none-any.whl";

            var osReqsParam = string.Empty;
            if (requiredOsPackages != null)
            {
                osReqsParam = $"--os-requirements {string.Join(',', requiredOsPackages)}";
            }

            // pypi package url usually is in following format
            //https://pypi.io/packages/source/{ package_name_first_letter }/{ package_name }/{ package_name }-{ package_version }.tar.gz

            var pkgNameFirstLetter = pkgName.ElementAt(0);
            var pyPiTarUrl = $"https://pypi.io/packages/source/{pkgNameFirstLetter}/{pkgName}/{pkgName}-{pkgVersion}.tar.gz";

            if (string.IsNullOrEmpty(pkgTag))
            { 
                pkgTag = pkgVersion;
            }

            var script = new ShellScriptBuilder()
            // Fetch source code
                .AddCommand($"mkdir -p {pkgSrcDir} && git clone {gitRepoUrl} {pkgSrcDir}")
                .AddCommand($"cd {pkgSrcDir} && git checkout tags/{pkgTag} -b test/{pkgVersion}")
            // Build & package
                .AddBuildCommand($"{pkgSrcDir} --package -o {pkgBuildOutputDir} {osReqsParam}") // Should create a file <name>-<version>.tgz
                .AddFileExistsCheck(oryxPackTarOutput)
                .AddFileExistsCheck(oryxPackEggOutput)
                .AddFileExistsCheck(oryxPackWheelOutput)
           // Compute diff between tar contents
           // Download public PyPi tar for comparison
                .AddCommand($"export PyPiTarUrl={pyPiTarUrl}")
                .AddCommand($"wget -O {pypiTarPath} $PyPiTarUrl")
          // Print tar content lists
                .AddCommand("echo " + tarListMarker)
                .AddCommand($"{tarListCmd} {oryxPackTarOutput}")
                .AddCommand("echo " + tarListMarker)
                .AddCommand($"{tarListCmd} {pypiTarPath}")
                .ToString();

            // Act
            // Not using Settings.BuildImageName on purpose - so that apt-get can run as root
            var image = _imageHelper.GetBuildImage();
            var result = _dockerCli.Run(image, "/bin/bash", new[] { "-c", script });

            // Assert contained file names
            var tarLists = result.StdOut.Split(tarListMarker);

            var (oryxTarList, oryxTarSize) = ParseTarList(tarLists[1]);
            var (pypiTarList, pypiTarSize) = ParseTarList(tarLists[2]);

            var unContained = pypiTarList.Where(x => !oryxTarList.Contains(x));
            Assert.Equal(pypiTarList, oryxTarList);
            
            // Assert tar file sizes
            var tarSizeDiff = Math.Abs(pypiTarSize - oryxTarSize);
            Assert.True(tarSizeDiff <= pypiTarSize * 0.1, // Accepting differences of less than 10% of the official artifact size
                $"Size difference is too big. Oryx build: {oryxTarSize}, Actual PyPi: {pypiTarSize}");
        }

        private (IEnumerable<string>, int) ParseTarList(string rawTarList)
        {
            var fileEntries = rawTarList.Trim().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Where(line => !line.StartsWith('d')) // Filter out directories
                .Select(line => line.Split(new char[0], StringSplitOptions.RemoveEmptyEntries))
                .Select(cols => (Size: int.Parse(cols[2]), Name: cols.Last())) // Keep only the size and the name
                .Where(entry => IgnoredTarEntries.Contains(entry.Name))
                .OrderBy(entry => entry.Name);

            return (fileEntries.Select(entry => entry.Name), fileEntries.Sum(entry => entry.Size));
        }
    }
}