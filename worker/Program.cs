using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        private static string redisHostname = "master.lks-redis.3qhbn9.use1.cache.amazonaws.com:6379";

        public static int Main(string[] args)
        {
            try
            {
                var pgsql = OpenDbConnection("Server=lks-rdss.c5k659fmsxgw.us-east-1.rds.amazonaws.com;Username=postgres;Password=postgres;");
                var redisConn = OpenRedisConnection(redisHostname);
                var redis = redisConn.GetDatabase();

                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };

                while (true)
                {
                    Thread.Sleep(100);

                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Redis disconnected. Reconnecting...");
                        redisConn = OpenRedisConnection(redisHostname);
                        redis = redisConn.GetDatabase();
                    }

                    try
                    {
                        string json = redis.ListLeftPopAsync("votes").Result;
                        if (json != null)
                        {
                            var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                            Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");

                            if (pgsql.State != System.Data.ConnectionState.Open)
                            {
                                Console.WriteLine("PostgreSQL disconnected. Reconnecting...");
                                pgsql = OpenDbConnection("Server=lks-rdss.c5k659fmsxgw.us-east-1.rds.amazonaws.com;Username=postgres;Password=postgres;");
                            }

                            UpdateVote(pgsql, vote.voter_id, vote.vote);
                        }
                        else
                        {
                            keepAliveCommand.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Runtime error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex}");
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for db (SocketException)");
                    Thread.Sleep(1000);
                }
                catch (DbException)
                {
                    Console.Error.WriteLine("Waiting for db (DbException)");
                    Thread.Sleep(1000);
                }
            }

            Console.WriteLine("Connected to PostgreSQL");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            while (true)
            {
                try
                {
                    string ipAddress = GetIp(hostname);
                    Console.WriteLine($"Resolved Redis host '{hostname}' to IP: {ipAddress}");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Waiting for Redis: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
        {
            try
            {
                return Dns.GetHostEntryAsync(hostname)
                    .Result
                    .AddressList
                    .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToString();
            }
            catch (Exception ex)
            {
                throw new Exception($"DNS resolution failed for '{hostname}': {ex.Message}");
            }
        }

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            using (var command = connection.CreateCommand())
            {
                try
                {
                    command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                    command.Parameters.AddWithValue("@id", voterId);
                    command.Parameters.AddWithValue("@vote", vote);
                    command.ExecuteNonQuery();
                }
                catch (DbException)
                {
                    command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
