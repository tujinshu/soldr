﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuildDependencyReader.ProjectFileParser;
using QuickGraph;
using QuickGraph.Algorithms;

namespace BuildDependencyReader.BuildDependencyResolver
{
    public class BuildDependencyResolver
    {
        public const string CSPROJ_EXTENSION = ".csproj";
        public const string SLN_EXTENSION = ".sln";


        public static IEnumerable<Project> BuildOrder(IProjectFinder projectFinder, IEnumerable<Project> projects)
        {
            return ProjectDependencyGraph(projectFinder, projects, true).TopologicalSort();
        }

        /// <summary>
        /// Creates a graph representing all the dependencies within the given projects. 
        /// The edges will be from dependent project to dependency, unless <paramref name="reverse"/> is True, 
        /// in which case the edges will be from dependency to dependent (which is more useful for topological sorting - 
        /// which in this way will return the projects in build order)
        /// </summary>
        /// <param name="projects"></param>
        /// <param name="direction"></param>
        /// <returns></returns>
        public static AdjacencyGraph<Project, SEdge<Project>> ProjectDependencyGraph(IProjectFinder projectFinder, IEnumerable<Project> projects, bool reverse)
        {
            return DeepDependencies(projectFinder, projects, false)
                    .Distinct()
                    .Select(x => new SEdge<Project>(reverse ? x.Key : x.Value, reverse ? x.Value : x.Key))
                    .ToAdjacencyGraph<Project, SEdge<Project>>(false);
        }


        public static AdjacencyGraph<String, SEdge<String>> SolutionDependencyGraph(IProjectFinder projectFinder, IEnumerable<Project> projects, bool reverse)
        {
            return DeepDependencies(projectFinder, projects, true)
                    .Where(x => x.Key != x.Value)
                    .Select(x => ProjectEdgeToSLNEdge(projectFinder, x))
                    .Where(x => false == x.Key.ToLowerInvariant().Equals(x.Value.ToLowerInvariant()))
                    .Distinct()
                    .Select(x => new SEdge<String>(reverse ? x.Key : x.Value, reverse ? x.Value : x.Key))
                    .ToAdjacencyGraph<String, SEdge<String>>(false);
        }

        /// <summary>
        /// Builds a graph of dependencies between solution files, from a list of .csproj and .sln files. 
        /// </summary>
        /// <param name="inputFiles">Project (.csproj) and solution (.sln) files to start the dependency search from</param>
        /// <param name="_excludedSLNs">Solution (.sln) files that should be excluded from the final dependency graph - useful for temporarily ignoring cyclic dependencies. 
        /// Note that .sln files may appear in the final graph even if they are not given in the input files list, if something in the input depends on them.</param>
        /// <param name="basePath">Base path to start search for dependency .sln and .csproj files (used mainly for resolving assembly references)</param>
        public static BuildDependencyInfo DependencyInfo(IEnumerable<string> inputFiles, IEnumerable<string> _excludedSLNs, string basePath, bool verbose)
        {
            string[] projectFiles;
            string[] slnFiles;
            ProcessInputFiles(inputFiles.Select(CanonicalPath), out projectFiles, out slnFiles);

            var excludedSLNs = _excludedSLNs.Select(CanonicalPath)
                                            .Select(x => x.ToLowerInvariant())
                                            .ToArray();

            if (excludedSLNs.Any(x => false == SLN_EXTENSION.Equals(System.IO.Path.GetExtension(x))))
            {
                throw new ArgumentException("excluded files must have extension: " + SLN_EXTENSION, "_excludedSLNs");
            }

            var csprojProjects = projectFiles.Select(Project.FromCSProj);

            var projectFinder = new ProjectFinder(basePath, true);
            var slnProjects = slnFiles.SelectMany(projectFinder.GetProjectsOfSLN);

            var projects = csprojProjects.Union(slnProjects).ToArray();
            if (verbose)
            {
                PrintInputInfo(excludedSLNs, projectFiles, slnFiles, projects);
            }

            return new BuildDependencyInfo(ProjectDependencyGraph(projectFinder, projects, false),
                                           SolutionDependencyGraph(projectFinder, projects, false), 
                                           excludedSLNs);
        }

        private static IEnumerable<Project> GetAllProjectsInSolutionsOfProject(IProjectFinder projectFinder, Project project)
        {
            return projectFinder.GetProjectsOfSLN(projectFinder.GetSLNFileForProject(project));
        }

        private static KeyValuePair<string, string> ProjectEdgeToSLNEdge(IProjectFinder projectFinder, KeyValuePair<Project, Project> x)
        {
            return new KeyValuePair<String, String>(projectFinder.GetSLNFileForProject(x.Key).FullName,
                                                    projectFinder.GetSLNFileForProject(x.Value).FullName);
        }


        public static IEnumerable<KeyValuePair<Project, Project>> DeepDependencies(IProjectFinder projectFinder, IEnumerable<Project> projects, bool includeAllProjectsInSolution)
        {
            var projectsToTraverse = new Queue<KeyValuePair<Project, Project>>(projects.Select(x => new KeyValuePair<Project, Project>(x, x)));

            var traversedProjects = new HashSet<Project>();

            while (projectsToTraverse.Any())
            {
                var projectPair = projectsToTraverse.Dequeue();
                var project = projectPair.Value;

                if (projectPair.Key != projectPair.Value)
                {
                    yield return projectPair;
                }

                if (traversedProjects.Contains(project))
                {
                    continue;
                }
                traversedProjects.Add(project);

                if (includeAllProjectsInSolution)
                {
                    foreach (var projectInSameSolution in GetAllProjectsInSolutionsOfProject(projectFinder, project)
                                                            .Where(x => false == traversedProjects.Contains(x)))
                    {
                        projectsToTraverse.Enqueue(new KeyValuePair<Project, Project>(projectInSameSolution, projectInSameSolution));
                    }
                }
                foreach (var subProject in project.ProjectReferences)
                {
                    projectsToTraverse.Enqueue(new KeyValuePair<Project, Project>(project, subProject));
                }
                if (null != projectFinder)
                {
                    foreach (var assemblySubProject in project.AssemblyReferences.SelectMany(projectFinder.FindProjectForAssemblyReference))
                    {
                        projectsToTraverse.Enqueue(new KeyValuePair<Project, Project>(project, assemblySubProject));
                    }
                }
            }
        }

        private static void ProcessInputFiles(IEnumerable<string> inputFiles, out string[] projectFiles, out string[] slnFiles)
        {
            slnFiles = new string[] { };
            projectFiles = new string[] { };
            var filesByExtensions = inputFiles.GroupBy(System.IO.Path.GetExtension);
            foreach (var extensionGroup in filesByExtensions)
            {
                switch (extensionGroup.Key)
                {
                    case CSPROJ_EXTENSION:
                        projectFiles = extensionGroup.ToArray();
                        break;
                    case SLN_EXTENSION:
                        slnFiles = extensionGroup.ToArray();
                        break;

                    default:
                        throw new ArgumentException(String.Format("Unknown file type: '{0}' in {1}", extensionGroup.Key, String.Join(", ", extensionGroup)), "_inputFiles");
                }
            }
        }

        private static void PrintInputInfo(string[] excludedSLNs, IEnumerable<string> projectFiles, IEnumerable<string> slnFiles, Project[] projects)
        {
            Console.Error.WriteLine("Input CSPROJ files:\n\t" + String.Join("\n\t", projectFiles));
            Console.Error.WriteLine("Input SLN files:\n\t" + String.Join("\n\t", slnFiles));
            Console.Error.WriteLine("Input projects:\n\t" + String.Join("\n\t", projects.Select(x => x.Path)));

            Console.Error.WriteLine("Excluding solutions:\n\t" + String.Join("\n\t", excludedSLNs));
        }



        private static string CanonicalPath(string x)
        {
            return System.IO.Path.GetFullPath(x.Trim());
        }

    }
}
