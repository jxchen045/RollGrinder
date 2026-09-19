using System.Runtime.CompilerServices;

// 仿真实现只允许由组合根装配；测试项目需要断言装配结果。
[assembly: InternalsVisibleTo("RollGrinder.Composition")]
[assembly: InternalsVisibleTo("RollGrinder.Integration.Tests")]
