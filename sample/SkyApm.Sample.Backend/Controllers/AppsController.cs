using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using SkyApm.Sample.Backend.Models;
using SkyApm.Tracing;

namespace SkyApm.Sample.Backend.Controllers
{
    [Route("api/[controller]")]
    public class AppsController: Controller
    {
        private readonly SampleDbContext _dbContext;
        private readonly ITracingContext _tracingContext;
        private readonly IExitSegmentContextAccessor _contextAccessor;

        public AppsController(SampleDbContext sampleDbContext,
            ITracingContext tracingContext,
            IExitSegmentContextAccessor contextAccessor)
        {
            _dbContext = sampleDbContext;
            _tracingContext = tracingContext;
            _contextAccessor = contextAccessor;
        }

        [HttpGet]
        public IEnumerable<Application> Get()
        {
            return _dbContext.Applications.ToList();
        }

        [HttpGet("{id}")]
        public Application Get(int id)
        {
            return _dbContext.Applications.Find(id);
        }

        [HttpPut]
        public void Put([FromBody]Application application)
        {
            _dbContext.Applications.Add(application);
            _dbContext.SaveChanges();
        }

        [HttpGet]
        [Route("test")]
        public async Task<IActionResult> Test()
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (var connection = new SqlConnection($"Data Source=.;Initial Catalog=tempdb;Integrated Security=True"))
            {
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync();

                var a = _contextAccessor.Context; // not null , but should be null

                //use dapper no problem, inner has await mark method , but leak release a context , so wait gc to collect it
                await connection.ExecuteReaderAsync("SELECT 1");
            }

            sw.Stop();
            return Json(sw.ElapsedMilliseconds);
        }

        [HttpGet]
        [Route("test2")]
        public async Task<IActionResult> Test2()
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            var task1 = LocalTest();
            var task2 = LocalTest();

            await task1;
            await task2;

            sw.Stop();
            return Json(sw.ElapsedMilliseconds);
        }

        public Task LocalTest()
        {
            var context = _tracingContext.CreateLocalSegmentContext("Test");

            var tcs = new TaskCompletionSource<int>();
            _ = Task.Delay(10).ContinueWith(n =>
            {
                // in ContinueWith will not restore ContextAccessor Context , just finish span
                _tracingContext.Release(context);
                tcs.SetResult(1);
            });
            return tcs.Task;
            // should at method end restore ContextAccessor  Context
        }


        public static Task TestAsync()
        {
            var stateMachine = new TestAsyncStateMachine
            {
                builder = AsyncTaskMethodBuilder.Create(),
                state = -1
            };
            stateMachine.builder.Start(ref stateMachine);
            return stateMachine.builder.Task;
        }

        private sealed class TestAsyncStateMachine : IAsyncStateMachine
        {
            public int state;
            public AsyncTaskMethodBuilder builder;
            private TaskAwaiter taskAwaiter;

            public void MoveNext()
            {
                int num = state;
                try
                {
                    TaskAwaiter awaiter;
                    if (num != 0)
                    {
                        awaiter = Task.Delay(5000).GetAwaiter();
                        if (!awaiter.IsCompleted)
                        {
                            state = num = 0;
                            taskAwaiter = awaiter;
                            var stateMachine = this;
                            builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
                            return;
                        }
                    }
                    else
                    {
                        awaiter = taskAwaiter;
                        taskAwaiter = new TaskAwaiter();
                        state = num = -1;
                    }

                    awaiter.GetResult();

                    Console.WriteLine("1");
                }
                catch (Exception exception)
                {
                    state = -2;
                    builder.SetException(exception);
                    return;
                }

                state = -2;
                builder.SetResult();
            }

            public void SetStateMachine(IAsyncStateMachine stateMachine)
            {
            }
        }
    }
}