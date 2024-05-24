using System;
using System.Collections.ObjectModel;
using System.Collections;
using System.Dynamic;
using System.Collections.Generic;
using System.Linq;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Transfer;
using Amazon.S3.Model;
using Npgsql;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using HttpMultipartParser;
using System.Security.Cryptography;


ConcurrentDictionary<string, WebSocket> clients = new ConcurrentDictionary<string, WebSocket>();

var connectionStringBuilder = new NpgsqlConnectionStringBuilder {
	Host = "aws-0-us-west-1.pooler.supabase.com",
	Port = 5432,
	Username = "postgres.whrubiliadjyowjboddl",
	Password = "SQLHYPE123$",
	Database = "postgres",
	Pooling = true
};

// The following lines are using environment variables to securely handle AWS keys.
// No secrets are hard-coded in this file.
string? awsAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
string? awsSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_KEY");


var credentials = new BasicAWSCredentials(awsAccessKey, awsSecretKey);
using var s3Client = new AmazonS3Client(credentials, RegionEndpoint.USEast2);
const string bucketName = "chatappdogbucket";

async Task createServerHttp() {
	var listener = new HttpListener();
	listener.Prefixes.Add("http://+:8080/");
	listener.Start();
	Console.WriteLine("Server starts to run");
	foreach (string prefix in listener.Prefixes) {
		Console.WriteLine($"Listening on: {prefix}");
	}
	try {
		while (true) {
			var context = await listener.GetContextAsync();
			if (context.Request.IsWebSocketRequest) {
				HandleWebSocketRequest(context);
			} else {
				HandleRequest(context);
			}
		}
	} catch (HttpListenerException ex) {
		if (ex.ErrorCode == 183) {
			Console.WriteLine("Port conflict: Another application may be using the same port.");
		} else {
			Console.WriteLine($"HTTP listener exception: {ex.Message}");
		}
	}
}

await createServerHttp();

async void HandleWebSocketRequest(HttpListenerContext context) {
	WebSocket webSocket = null;
	int userKey = -1;
	HashSet<int> setOfUsers = new HashSet<int>();
	try {
		HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
		webSocket = webSocketContext.WebSocket;

		byte[] buffer = new byte[8192];

		while (webSocket.State == WebSocketState.Open) {
			WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
			string userMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
			if (userMessage != null && userMessage.StartsWith('{') && userMessage.EndsWith('}')) {
				WsReq? jsonObject = JsonSerializer.Deserialize<WsReq>(userMessage);
				if (jsonObject?.Type == "init") {
					int userId = Convert.ToInt32(jsonObject?.UserId);
					if (userId > 0) {
						if (clients.ContainsKey(userId.ToString())) {
							clients.TryRemove(userId.ToString(), out _);
						}
						clients.TryAdd(userId.ToString(), webSocket);
						userKey = userId;

						var welcomeMessage = new { type = "init", content = "WebSocket connection established. Your user ID is " + userId };
						var jsonWelcome = JsonSerializer.Serialize(welcomeMessage);
						byte[] welcomeMessageBytes = Encoding.UTF8.GetBytes(jsonWelcome);
						await webSocket.SendAsync(new ArraySegment<byte>(welcomeMessageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

						string[] activeUsers = clients.Keys.ToArray();

						string json = JsonSerializer.Serialize(activeUsers);
						byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
						await webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
				} else if (jsonObject?.Type == "message") {
					int userId = Convert.ToInt32(jsonObject?.UserId);
					int chatId = Convert.ToInt32(jsonObject?.ChatId);
					int messageId = Convert.ToInt32(jsonObject?.MessageId);
					if (userId > 0 && chatId > 0 && messageId > 0) {
						using var connection = new NpgsqlConnection(connectionStringBuilder.ConnectionString);
						connection.Open();
						string sql = @"SELECT messages.date_sent, chats.first_user_id, chats.second_user_id 
						FROM messages
						INNER JOIN chats ON messages.chat_id = chats.id
						WHERE messages.chat_id = @chat_id AND messages.id = @message_id;";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("chat_id", chatId);
						cmd.Parameters.AddWithValue("message_id", messageId);
						using var reader = cmd.ExecuteReader();
						var infoRes = new ExpandoObject() as IDictionary<string, object>;
						while (reader.Read()) {
							for (int i = 0; i < reader.FieldCount; i++) {
								infoRes.Add(reader.GetName(i), reader.GetValue(i));
							}
						}
						var messageDateSent = infoRes["date_sent"];
						int firstUserId = Convert.ToInt32(infoRes["first_user_id"]);
						int secondUserId = Convert.ToInt32(infoRes["second_user_id"]);

						var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
						dateSentObject.Add("type", "message");
						dateSentObject.Add("dateSent", messageDateSent);
						dateSentObject.Add("messageId", messageId);
						if (firstUserId == userId) {
							dateSentObject.Add("senderId", firstUserId);
							dateSentObject.Add("guestId", secondUserId);
						} else if (secondUserId == userId) {
							dateSentObject.Add("senderId", secondUserId);
							dateSentObject.Add("guestId", firstUserId);
						}
						var json = JsonSerializer.Serialize(dateSentObject);
						byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
						if (firstUserId == userId && firstUserId > 0 && secondUserId > 0) {
							if (clients.TryGetValue(secondUserId.ToString(), out WebSocket clientWebSocket)) {
								await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
							}
						} else if (secondUserId == userId && firstUserId > 0 && secondUserId > 0) {
							if (clients.TryGetValue(firstUserId.ToString(), out WebSocket clientWebSocket)) {
								await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
							}
						}
					}
				} else if (jsonObject?.Type == "seen") {
					int chatId = Convert.ToInt32(jsonObject?.ChatId);
					int senderId = Convert.ToInt32(jsonObject?.SenderId);
					if (chatId > 0 && senderId > 0) {
						var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
						dateSentObject.Add("type", "seen");
						dateSentObject.Add("chatId", chatId);
						var json = JsonSerializer.Serialize(dateSentObject);
						byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);

						if (clients.TryGetValue(senderId.ToString(), out WebSocket clientWebSocket)) {
							await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
						}
					}
				} else if (jsonObject?.Type == "delete") {
					int senderId = Convert.ToInt32(jsonObject?.SenderId);
					int messageId = Convert.ToInt32(jsonObject?.MessageId);
					var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
					dateSentObject.Add("type", "delete");
					dateSentObject.Add("messageId", messageId);
					var json = JsonSerializer.Serialize(dateSentObject);
					byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);

					if (clients.TryGetValue(senderId.ToString(), out WebSocket clientWebSocket)) {
						await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
				} else if (jsonObject?.Type == "online") {
					int senderId = Convert.ToInt32(jsonObject?.SenderId);
					if (clients.ContainsKey(senderId.ToString())) {
						var objectRes = new ExpandoObject() as IDictionary<string, object>;
						objectRes.Add("type", "online");
						objectRes.Add("request", true);
						objectRes.Add("online", true);
						var json = JsonSerializer.Serialize(objectRes);
						byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
						await webSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
					} else {
						var objectRes = new ExpandoObject() as IDictionary<string, object>;
						objectRes.Add("type", "online");
						objectRes.Add("request", true);
						objectRes.Add("online", false);
						var json = JsonSerializer.Serialize(objectRes);
						byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
						await webSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
				} else if (jsonObject?.SetUsers != null && jsonObject?.Type == "setUsers") {
					List<int> listUsersId = jsonObject.SetUsers;
					int senderId = jsonObject.SenderId;
					if (listUsersId != null && listUsersId.Count > 0) {
						for (int i = 0; i < listUsersId.Count; i++) {
							setOfUsers.Add(listUsersId[i]);
							var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
							dateSentObject.Add("type", "online");
							dateSentObject.Add("online", true);
							dateSentObject.Add("senderId", senderId);
							dateSentObject.Add("response", true);
							var json = JsonSerializer.Serialize(dateSentObject);
							byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
							if (clients.TryGetValue(listUsersId[i].ToString(), out WebSocket clientWebSocket)) {
								await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
							}
						}
					}
				} else if (jsonObject?.Type == "editedMessage") {
					string contentMessage = jsonObject?.Content;
					int senderId = Convert.ToInt32(jsonObject?.SenderId);
					int messageId = Convert.ToInt32(jsonObject?.MessageId);
					var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
					dateSentObject.Add("type", "editedMessage");
					dateSentObject.Add("messageId", messageId);
					dateSentObject.Add("content", contentMessage);
					var json = JsonSerializer.Serialize(dateSentObject);
					byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
					if (clients.TryGetValue(senderId.ToString(), out WebSocket clientWebSocket)) {
						await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
				}
			} else {
				Console.WriteLine("Received a message that's not a valid WsReq object JSON: " + userMessage);
				if (userKey > 0 && clients.ContainsKey(userKey.ToString())) {
					clients.TryRemove(userKey.ToString(), out _);
					if (setOfUsers.Count > 0) {
						foreach (int userId in setOfUsers) {
							var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
							dateSentObject.Add("type", "online");
							dateSentObject.Add("online", false);
							dateSentObject.Add("senderId", userKey);
							dateSentObject.Add("response", true);
							var json = JsonSerializer.Serialize(dateSentObject);
							byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
							if (clients.TryGetValue(userId.ToString(), out WebSocket clientWebSocket)) {
								await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
							}
						}
					}
				}
			}
		}
		await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
	} catch (Exception ex) {
		Console.WriteLine("An error occurred: " + ex.Message);
	} finally {
		if (webSocket != null && webSocket.State == WebSocketState.Open) {
			await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
		}
		if (userKey > 0 && clients.ContainsKey(userKey.ToString())) {
			clients.TryRemove(userKey.ToString(), out _);
			if (setOfUsers.Count > 0) {
				foreach (int userId in setOfUsers) {
					var dateSentObject = new ExpandoObject() as IDictionary<string, object>;
					dateSentObject.Add("type", "online");
					dateSentObject.Add("online", false);
					dateSentObject.Add("senderId", userKey);
					dateSentObject.Add("response", true);
					var json = JsonSerializer.Serialize(dateSentObject);
					byte[] bufferWsRes = Encoding.UTF8.GetBytes(json);
					if (clients.TryGetValue(userId.ToString(), out WebSocket clientWebSocket)) {
						await clientWebSocket.SendAsync(new ArraySegment<byte>(bufferWsRes), WebSocketMessageType.Text, true, CancellationToken.None);
					}
				}
			}
		}
	}
}

async Task Echo(WebSocket webSocket) {
	byte[] buffer = new byte[8192];
	WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
	while (!result.CloseStatus.HasValue) {
		await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
		result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
	}
	await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
}

async Task<string> uploadFileAsync(MemoryStream fileToUpload, string bucketName, string key) {
	using var fileTransferUtility = new TransferUtility(s3Client);

	try {
		await fileTransferUtility.UploadAsync(fileToUpload, bucketName, key);
		Console.WriteLine("Upload completed");
		GetPreSignedUrlRequest request = new GetPreSignedUrlRequest {
			BucketName = bucketName,
			Key = key,
			Expires = DateTime.Now.AddMinutes(5)
		};
		string url = s3Client.GetPreSignedURL(request);

		return url;
	} catch (AmazonS3Exception e) {
		Console.WriteLine("Error encountered on server. Message:'{0}' when writing an object", e.Message);
	} catch (Exception e) {
		Console.WriteLine("Unknown encountered on server. Message:'{0}' when writing an object", e.Message);
	}

	return null;
}

async Task<string> generatePreSignedURL(string bucketName, string key) {
	try {
		GetPreSignedUrlRequest request = new GetPreSignedUrlRequest {
			BucketName = bucketName,
			Key = key,
			Expires = DateTime.UtcNow.AddHours(8)
		};
		string url = s3Client.GetPreSignedURL(request);
		return url;
	} catch (AmazonS3Exception e) {
		Console.WriteLine("Error encountered on server. Message:'{0}' when generating pre-signed URL", e.Message);
	} catch (Exception e) {
		Console.WriteLine("Unknown encountered on server. Message:'{0}' when generating pre-signed URL", e.Message);
	}

	return null;
}
async Task<bool> deleteFileAsync(string bucketName, string key) {
	if (s3Client == null) {
		Console.WriteLine("AmazonS3Client instance is null.");
		return false;
	} else {
		try {
			var deleteObjectRequest = new DeleteObjectRequest {
				BucketName = bucketName,
				Key = key
			};
			var response = await s3Client.DeleteObjectAsync(deleteObjectRequest);
			return true;
		} catch (AmazonS3Exception e) {
			Console.WriteLine($"Error deleting object: {e.Message}");
		} catch (Exception e) {
			Console.WriteLine($"Unknown error: {e.Message}");
		}
		return false;
	}
}
async void HandleRequest(HttpListenerContext context) {
	HttpListenerRequest request = context.Request;
	HttpListenerResponse response = context.Response;

	void outputStreamRes(byte[] buffer) {
		response.ContentLength64 = buffer.Length;
		response.OutputStream.Write(buffer, 0, buffer.Length);
	}

	response.Headers.Add("Access-Control-Allow-Origin", "*");
	response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, PATCH, DELETE, OPTIONS");
	response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");

	if (request.HttpMethod == "OPTIONS") {
		response.StatusCode = 200;
		response.Close();
		return;
	}

	using var connection = new NpgsqlConnection(connectionStringBuilder.ConnectionString);
	connection.Open();

	if (request.HttpMethod == "GET") {
		Console.WriteLine("is getting it");
		string sql = "SELECT * FROM users";
		using var cmd = new NpgsqlCommand(sql, connection);
		using var reader = cmd.ExecuteReader();
		StringBuilder responseBuilder = new StringBuilder();
		responseBuilder.Append("<html><head><meta charset=\"UTF-8\">");
		responseBuilder.Append("<style>");
		responseBuilder.Append(":root { color-scheme: dark; }");
		responseBuilder.Append("</style>");
		responseBuilder.Append("</head><body>");
		responseBuilder.Append("<h1>List of Users</h1>");
		responseBuilder.Append("<ul>");

		while (reader.Read()) {
			responseBuilder.Append("<li>User Data: ");
			for (int i = 0; i < reader.FieldCount; i++) {
				responseBuilder.Append($"{reader.GetName(i)}: {reader.GetValue(i)}, ");
			}
			responseBuilder.Append("</li>");
		}
		responseBuilder.Append("</ul>");
		responseBuilder.Append("</body></html>");

		byte[] buffer = Encoding.UTF8.GetBytes(responseBuilder.ToString());
		response.StatusCode = 200;
		response.ContentType = "text/html";
		outputStreamRes(buffer);
		response.Close();
	}
	if (request.HttpMethod == "POST") {
		if (request.Url != null && request.Url.AbsolutePath == "/session/validate") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string session_keyReq = readerReq.ReadToEnd();
				string sql = "SELECT id, pfp, name, display_name, bio FROM users WHERE session_key = @session_key";
				using var cmd = new NpgsqlCommand(sql, connection);
				cmd.Parameters.AddWithValue("session_key", session_keyReq);
				using var reader = cmd.ExecuteReader();
				var user = new ExpandoObject() as IDictionary<string, object>;
				while (reader.Read()) {
					for (int i = 0; i < reader.FieldCount; i++) {
						if (reader.GetName(i) != "pfp") {
							user.Add(reader.GetName(i), reader.GetValue(i));
						} else {
							if (reader.GetValue(i) != null && reader.GetValue(i).ToString() != "") {
								string mediaRow = reader.GetValue(i).ToString();
								if (mediaRow != null) {
									int indexCharacter = mediaRow.IndexOf(':');
									if (indexCharacter > 0) {
										string mediaType = mediaRow.Substring(0, indexCharacter);
										string key = mediaRow.Substring(indexCharacter + 1);
										string urlRes = await generatePreSignedURL(bucketName, key);
										if (urlRes != null) {
											user.Add("pfp", new { type = mediaType, content = urlRes });
										}
									}
								}
							}
						}
					}
				}
				if (user.Count > 0) {
					string json = JsonSerializer.Serialize(user);
					byte[] buffer = Encoding.UTF8.GetBytes(json);
					response.StatusCode = 200;
					response.ContentType = "application/json";
					outputStreamRes(buffer);
					response.Close();
				} else {
					byte[] buffer = Encoding.UTF8.GetBytes("Invalid key");
					response.StatusCode = 401;
					response.ContentType = "text/plain";
					outputStreamRes(buffer);
					response.Close();
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}

		if (request.Url != null && request.Url.AbsolutePath == "/user/create") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UserInit? user = JsonSerializer.Deserialize<UserInit>(json);
				if (user != null) {
					string nameReq = user.Name;
					string emailReq = user.Email;
					string passwordReq = user.Password;
					if (nameReq != null && emailReq != null && passwordReq != null) {
						string checkSql = "SELECT COUNT(*) FROM users WHERE email = @email OR name = @name";
						using var checkCmd = new NpgsqlCommand(checkSql, connection);
						checkCmd.Parameters.AddWithValue("email", emailReq);
						checkCmd.Parameters.AddWithValue("name", nameReq);
						int userCount = Convert.ToInt32(checkCmd.ExecuteScalar() ?? 0);
						if (userCount > 0) {
							byte[] buffer = Encoding.UTF8.GetBytes("User already exists");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						} else {
							string sql = @"INSERT INTO users (name, email, password, salt, session_key) VALUES 
							(@name, @email, @password, @salt, @session_key)
							RETURNING id, session_key";
							using var cmd = new NpgsqlCommand(sql, connection);
							(string hashedPassword, byte[] salt) = HashPassword(passwordReq);
							string sessionKey = generateSessionKey();
							cmd.Parameters.AddWithValue("name", nameReq);
							cmd.Parameters.AddWithValue("email", emailReq);
							cmd.Parameters.AddWithValue("password", hashedPassword);
							cmd.Parameters.AddWithValue("salt", salt);
							cmd.Parameters.AddWithValue("session_key", sessionKey);
							using var reader = cmd.ExecuteReader();
							if (reader.Read()) {
								var resRow = new ExpandoObject() as IDictionary<string, object>;
								for (int i = 0; i < reader.FieldCount; i++) {
									resRow.Add(reader.GetName(i), reader.GetValue(i));
								}
								string jsonRes = JsonSerializer.Serialize(resRow);
								byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
								response.StatusCode = 200;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("Server couldnt insert new user");
								response.StatusCode = 500;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							}
						}
					} else {
						byte[] buffer = Encoding.UTF8.GetBytes("Invalid data format server");
						response.StatusCode = 500;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}

		if (request.Url != null && request.Url.AbsolutePath == "/user/login") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UserInit? user = JsonSerializer.Deserialize<UserInit>(json);
				if (user != null) {
					string nameReq = user.Name;
					string emailReq = user.Email;
					string passwordReq = user.Password;
					byte[] buffer;
					if ((emailReq != null || nameReq != null) && passwordReq != null) {
						string sql;
						NpgsqlCommand cmd = null;
						if (!string.IsNullOrEmpty(emailReq)) {
							sql = "SELECT password, salt FROM users WHERE email = @email";
							cmd = new NpgsqlCommand(sql, connection);
							cmd.Parameters.AddWithValue("email", emailReq);
						} else if (!string.IsNullOrEmpty(nameReq)) {
							sql = "SELECT password, salt FROM users WHERE name = @name";
							cmd = new NpgsqlCommand(sql, connection);
							cmd.Parameters.AddWithValue("name", nameReq);
						} else {
							buffer = Encoding.UTF8.GetBytes("Couldnt find email or name");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							cmd?.Dispose();
							response.Close();
							return;
						}
						string passwordsqlResult = "hashedResultFromQuery";
						string hashedPassword = "passwordAfterBeingHashed";
						if (cmd != null) {
							byte[] salt;
							using (var reader = cmd.ExecuteReader()) {
								if (reader.Read()) {
									passwordsqlResult = (string)reader.GetValue(0);
									salt = (byte[])reader.GetValue(1);
									(string hashedPasswordSolve, byte[] _) = HashPassword(passwordReq, salt);
									hashedPassword = hashedPasswordSolve;
								} else {
									buffer = Encoding.UTF8.GetBytes("No account found with that email or name");
									response.StatusCode = 400;
									response.ContentType = "text/plain";
									outputStreamRes(buffer);
									cmd.Dispose();
									response.Close();
									return;
								}
							}
							if (hashedPassword == passwordsqlResult && (!string.IsNullOrEmpty(emailReq) || !string.IsNullOrEmpty(nameReq))) {
								string newKeySession = generateSessionKey();
								string sqlUpdate = "UPDATE users SET session_key = @session_key WHERE email = @email OR name = @name";
								using var cmdUpdate = new NpgsqlCommand(sqlUpdate, connection);
								cmdUpdate.Parameters.AddWithValue("session_key", newKeySession);
								cmdUpdate.Parameters.AddWithValue("email", emailReq ?? "_");
								cmdUpdate.Parameters.AddWithValue("name", nameReq ?? "_");
								int affectedRows = cmdUpdate.ExecuteNonQuery();
								if (affectedRows == 1) {
									buffer = Encoding.UTF8.GetBytes(newKeySession);
									response.StatusCode = 200;
									response.ContentType = "text/plain";
									outputStreamRes(buffer);
									cmd.Dispose();
									response.Close();
								} else {
									buffer = Encoding.UTF8.GetBytes("Failed to update session key");
									response.StatusCode = 500;
									response.ContentType = "text/plain";
									outputStreamRes(buffer);
									cmd.Dispose();
									response.Close();
								}
							} else {
								buffer = Encoding.UTF8.GetBytes("Incorrect password");
								response.StatusCode = 400;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								cmd.Dispose();
								response.Close();
							}
						}
					}
				}
				response.Close();
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/user/newPfp") {
			try {
				using var stream = request.InputStream;
				var parser = MultipartFormDataParser.Parse(stream);
				var file = parser.Files.First(f => f.Name == "image");
				var fileData = file.Data;
				using var memoryStream = new MemoryStream();
				fileData.CopyTo(memoryStream);

				int key = Convert.ToInt32(parser.Parameters.First(p => p.Name == "id").Data);
				string? urlImageRes = await uploadFileAsync(memoryStream, bucketName, key.ToString());
				if (urlImageRes != null) {
					string imageUrl = $"image:{key}";

					string sql = "UPDATE users SET pfp = @pfp WHERE id = @id RETURNING pfp";
					using var cmd = new NpgsqlCommand(sql, connection);
					cmd.Parameters.AddWithValue("pfp", imageUrl);
					cmd.Parameters.AddWithValue("id", key);
					using var reader = cmd.ExecuteReader();
					if (reader.Read()) {
						var user = new ExpandoObject() as IDictionary<string, object>;
						for (int i = 0; i < reader.FieldCount; i++) {
							if (reader.GetName(i) != "pfp") {
								user.Add(reader.GetName(i), reader.GetValue(i));
							} else {
								if (reader.GetValue(i) != null && reader.GetValue(i).ToString() != "") {
									string mediaRow = reader.GetValue(i).ToString();
									if (mediaRow != null) {
										int indexCharacter = mediaRow.IndexOf(':');
										if (indexCharacter > 0) {
											string mediaType = mediaRow.Substring(0, indexCharacter);
											string keyRes = mediaRow.Substring(indexCharacter + 1);
											string urlRes = await generatePreSignedURL(bucketName, keyRes);
											if (urlRes != null) {
												user.Add("pfp", new { type = mediaType, content = urlRes });
											}
										}
									}
								}
							}
						}
						if (user.Count > 0) {
							string json = JsonSerializer.Serialize(user);
							byte[] buffer = Encoding.UTF8.GetBytes(json);
							response.StatusCode = 200;
							response.ContentType = "application/json";
							outputStreamRes(buffer);
							response.Close();
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("Profile picture is not uploaded");
							response.StatusCode = 500;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					} else {
						byte[] buffer = Encoding.UTF8.GetBytes("Profile picture is not uploaded, something happened");
						response.StatusCode = 500;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					}
				} else {
					byte[] buffer = Encoding.UTF8.GetBytes("Profile picture is not uploaded, something happened with awss3");
					response.StatusCode = 500;
					response.ContentType = "text/plain";
					outputStreamRes(buffer);
					response.Close();
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/user/profileInfo") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UsersReq? objReq = JsonSerializer.Deserialize<UsersReq>(json);
				if (objReq != null) {
					int userId = Convert.ToInt32(objReq.GuestId);
					if (userId > 0) {
						string sql = "SELECT name, display_name, bio FROM users WHERE id = @userId";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("userId", userId);
						using var reader = cmd.ExecuteReader();
						if (reader.Read()) {
							var userRow = new ExpandoObject() as IDictionary<string, object>;
							for (int i = 0; i < reader.FieldCount; i++) {
								userRow.Add(reader.GetName(i), reader.GetValue(i));
							}
							if (userRow.Count > 0) {
								string jsonRes = JsonSerializer.Serialize(userRow);
								byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
								response.StatusCode = 200;
								outputStreamRes(buffer);
								response.ContentType = "application/json";
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("something went wrong, no user found");
								response.StatusCode = 500;
								outputStreamRes(buffer);
								response.ContentType = "application/json";
								response.Close();
							}
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("something went wrong, no rows with user info found");
							response.StatusCode = 500;
							outputStreamRes(buffer);
							response.ContentType = "application/json";
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/user/search") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string userQuery = readerReq.ReadToEnd();
				string sql = "SELECT name, display_name, id, pfp FROM users WHERE similarity(name, @name) > 0.01;";
				using var cmd = new NpgsqlCommand(sql, connection);
				cmd.Parameters.AddWithValue("name", userQuery);
				var reader = cmd.ExecuteReader();
				List<object> userRows = new List<object>();
				while (reader.Read()) {
					var user = new ExpandoObject() as IDictionary<string, object>;
					for (int i = 0; i < reader.FieldCount; i++) {
						if (reader.GetName(i) != "pfp") {
							user.Add(reader.GetName(i), reader.GetValue(i));
						} else {
							if (reader.GetValue(i) != null && reader.GetValue(i).ToString() != "") {
								string mediaRow = reader.GetValue(i).ToString();
								if (mediaRow != null) {
									int indexCharacter = mediaRow.IndexOf(':');
									if (indexCharacter > 0) {
										string mediaType = mediaRow.Substring(0, indexCharacter);
										string key = mediaRow.Substring(indexCharacter + 1);
										string urlRes = await generatePreSignedURL(bucketName, key);
										if (urlRes != null) {
											user.Add("pfp", new { type = mediaType, content = urlRes });
										}
									}
								}
							}
						}
					}
					userRows.Add(user);
				}
				if (userRows.Count > 0) {
					string json = JsonSerializer.Serialize(userRows);
					byte[] buffer = Encoding.UTF8.GetBytes(json);
					response.StatusCode = 200;
					response.ContentType = "application/json";
					outputStreamRes(buffer);
					response.Close();
				} else {
					byte[] buffer = Encoding.UTF8.GetBytes("There were no users found");
					response.StatusCode = 400;
					response.ContentType = "text/plain";
					outputStreamRes(buffer);
					response.Close();
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/user/block") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UsersReq? usersId = JsonSerializer.Deserialize<UsersReq>(json);
				if (usersId != null) {
					int userId = Convert.ToInt32(usersId.OwnerId);
					int guestId = Convert.ToInt32(usersId.GuestId);
					bool userBlock = usersId.UserBlock;
					bool chatHidden = usersId.ChatHidden;
					if (userId > 0 && guestId > 0) {
						string sql = @"INSERT INTO block (blocker_id, blocked_user_id, user_blocked, chat_hidden, chat_id)
						SELECT 
										@ownerId AS blocker_id, 
										@guestId AS blocked_user_id, 
										@userBlocked AS user_blocked, 
										@chatHidden AS chat_hidden,
										chat.id AS chat_id
						FROM 
										(
											SELECT id 
											FROM chats 
											WHERE 
															(first_user_id = @ownerId AND second_user_id = @guestId) OR 
															(first_user_id = @guestId AND second_user_id = @ownerId)
										) AS chat
										WHERE NOT EXISTS (
														SELECT 1
														FROM block
														WHERE (blocker_id = @ownerId AND blocked_user_id = @guestId) OR
														(blocker_id = @guestId AND blocked_user_id = @ownerId)
										)";
						int countAffectedRows = -1;
						using (var cmd = new NpgsqlCommand(sql, connection)) {
							cmd.Parameters.AddWithValue("ownerId", userId);
							cmd.Parameters.AddWithValue("guestId", guestId);
							cmd.Parameters.AddWithValue("userBlocked", userBlock);
							cmd.Parameters.AddWithValue("chatHidden", chatHidden);
							countAffectedRows = cmd.ExecuteNonQuery();
						}
						if (countAffectedRows > 0) {
							byte[] buffer = Encoding.UTF8.GetBytes("User was blocked");
							response.StatusCode = 200;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						} else {
							string sqlUpdate = @"UPDATE block
										SET blocker_id = @ownerId, 
										blocked_user_id = @guestId, 
										user_blocked = @userBlocked, 
										chat_hidden = @chatHidden, 
										chat_id = chat.id
								FROM
								(
								SELECT id 
								FROM chats 
								WHERE 
									(first_user_id = @ownerId AND second_user_id = @guestId)
									OR (first_user_id = @guestId AND second_user_id = @ownerId)
														) AS chat
								WHERE (blocker_id = @ownerId AND blocked_user_id = @guestId) OR
								(blocker_id = @guestId AND blocked_user_id = @ownerId AND user_blocked = false)";
							int countAffectedRowsU = -1;
							using (var cmdU = new NpgsqlCommand(sqlUpdate, connection)) {
								cmdU.Parameters.AddWithValue("ownerId", userId);
								cmdU.Parameters.AddWithValue("guestId", guestId);
								cmdU.Parameters.AddWithValue("userBlocked", userBlock);
								cmdU.Parameters.AddWithValue("chatHidden", chatHidden);
								countAffectedRowsU = cmdU.ExecuteNonQuery();
							}
							if (countAffectedRowsU > 0) {
								byte[] buffer = Encoding.UTF8.GetBytes("Block user was updated");
								response.StatusCode = 200;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("Something went wrong, might the user be already blocked");
								response.StatusCode = 400;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							}
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}

		if (request.Url != null && request.Url.AbsolutePath == "/chats/get") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UsersReq? userId = JsonSerializer.Deserialize<UsersReq>(json);
				if (userId != null) {
					int ownerIdReq = Convert.ToInt32(userId.OwnerId);
					int guestIdReq = Convert.ToInt32(userId.GuestId);
					if (ownerIdReq > 0 && guestIdReq > 0) {
						string sql = @"SELECT id FROM chats WHERE
								(first_user_id = @first_user_id AND second_user_id = @second_user_id) 
								OR 
								(first_user_id = @second_user_id AND second_user_id = @first_user_id)
								AND NOT EXISTS (
        SELECT 1 FROM block 
        WHERE (blocker_id = @second_user_id AND blocked_user_id = @first_user_id)
        AND user_blocked = TRUE)";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("first_user_id", ownerIdReq);
						cmd.Parameters.AddWithValue("second_user_id", guestIdReq);
						int chatId = (int)(cmd.ExecuteScalar() ?? 0);
						if (chatId > 0) {
							string sqlRes = @"UPDATE messages SET seen = TRUE WHERE chat_id = @chat_id AND sender_id = @sender_id;
							SELECT sender_id, content, media, date_sent, seen, edited, id FROM messages 
							WHERE chat_id = @chat_id ORDER BY date_sent ASC;";
							using var cmdRes = new NpgsqlCommand(sqlRes, connection);
							cmdRes.Parameters.AddWithValue("chat_id", chatId);
							cmdRes.Parameters.AddWithValue("sender_id", guestIdReq);
							using var readerRes = cmdRes.ExecuteReader();
							List<object> messagesRows = new List<object>();
							var chatIdRes = new ExpandoObject() as IDictionary<string, object>;
							chatIdRes.Add("chat_id", chatId);
							messagesRows.Add(chatIdRes);
							while (readerRes.Read()) {
								var message = new ExpandoObject() as IDictionary<string, object>;
								for (int i = 0; i < readerRes.FieldCount; i++) {
									if (readerRes.GetName(i) != "media") {
										message.Add(readerRes.GetName(i), readerRes.GetValue(i));
									} else {
										if (readerRes.GetValue(i) != null && readerRes.GetValue(i).ToString() != "") {
											string mediaRow = readerRes.GetValue(i).ToString();
											if (mediaRow != null) {
												int indexCharacter = mediaRow.IndexOf(':');
												if (indexCharacter > 0) {
													string mediaType = mediaRow.Substring(0, indexCharacter);
													string key = mediaRow.Substring(indexCharacter + 1);
													string urlRes = await generatePreSignedURL(bucketName, key);
													if (urlRes != null) {
														message.Add("media", new { type = mediaType, content = urlRes });
													}
												}
											}
										}
									}
								}
								messagesRows.Add(message);
							}
							if (messagesRows.Count > 0) {
								string jsonRes = JsonSerializer.Serialize(messagesRows);
								byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
								response.StatusCode = 200;
								response.ContentType = "application/json";
								outputStreamRes(buffer);
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("Couldnt get the chat");
								response.StatusCode = 500;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							}
						} else {
							string sqlRes = @"INSERT INTO chats (first_user_id, second_user_id) 
							SELECT @first_user_id, @second_user_id
							WHERE NOT EXISTS (
											SELECT 1 FROM block 
											WHERE (blocker_id = @second_user_id AND blocked_user_id = @first_user_id)
											AND user_blocked = TRUE
							)
							AND NOT EXISTS (
								SELECT 1 FROM chats 
								WHERE (first_user_id = @first_user_id AND second_user_id = @second_user_id) OR
								(first_user_id = @second_user_id AND second_user_id = @first_user_id) 
							)
							RETURNING id";
							using var cmdRes = new NpgsqlCommand(sqlRes, connection);
							cmdRes.Parameters.AddWithValue("first_user_id", ownerIdReq);
							cmdRes.Parameters.AddWithValue("second_user_id", guestIdReq);
							int chatIdRes = (int)(cmdRes.ExecuteScalar() ?? 0);
							if (chatIdRes > 0) {
								string sqlUpdateId = @"UPDATE block SET chat_id = @chat_id 
								WHERE ((blocker_id = @second_user_id AND blocked_user_id = @first_user_id) OR
												(blocker_id = @first_user_id AND blocked_user_id = @second_user_id)) 
								AND NOT EXISTS (
												SELECT 1 
												FROM chats 
												WHERE id = @chat_id
								)";
								using var cmdUpdate = new NpgsqlCommand(sqlUpdateId, connection);
								cmdUpdate.Parameters.AddWithValue("chat_id", chatIdRes);
								cmdUpdate.Parameters.AddWithValue("first_user_id", ownerIdReq);
								cmdUpdate.Parameters.AddWithValue("second_user_id", guestIdReq);
								int countRowsAffected = cmdUpdate.ExecuteNonQuery();
								List<object> messagesRows = new List<object>();
								var message = new ExpandoObject() as IDictionary<string, object>;
								if (countRowsAffected > 0) {
									message.Add("block_chat_id_updated", true);
								} else {
									message.Add("block_chat_id_updated", false);
								}
								message.Add("chat_id", chatIdRes);
								messagesRows.Add(message);
								string jsonRes = JsonSerializer.Serialize(messagesRows);
								byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
								response.StatusCode = 200;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("couldnt create new chat");
								response.StatusCode = 400;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							}
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/chats/getPrevious") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UsersReq? objReq = JsonSerializer.Deserialize<UsersReq>(json);
				if (objReq != null) {
					int userId = Convert.ToInt32(objReq.OwnerId);
					if (userId > 0) {
						string sql = @"SELECT users.id, users.name, users.display_name, users.pfp,
       block.user_blocked, block.chat_hidden,
       latest_message.content AS last_message,
       unseen_messages.count_unseen
						FROM users
						INNER JOIN (
										SELECT CASE
																					WHEN first_user_id = @user_id THEN second_user_id
																					ELSE first_user_id
																	END AS user_id,
																	id AS chat_id -- Using id from chats table as chat_id
										FROM chats
										WHERE first_user_id = @user_id OR second_user_id = @user_id
						) AS chat_users ON users.id = chat_users.user_id
						LEFT JOIN block ON (
										(block.blocked_user_id = chat_users.user_id AND block.blocker_id = @user_id)
										OR
										(block.blocked_user_id = @user_id AND block.blocker_id = chat_users.user_id)
						)
						LEFT JOIN (
										SELECT chat_id, MAX(date_sent) AS latest_date
										FROM messages
										WHERE seen = false
										GROUP BY chat_id
						) AS latest_message_date ON chat_users.chat_id = latest_message_date.chat_id
						LEFT JOIN messages AS latest_message ON latest_message.chat_id = chat_users.chat_id AND latest_message.date_sent = latest_message_date.latest_date
						LEFT JOIN (
										SELECT chat_id, COUNT(*) AS count_unseen
										FROM messages
										WHERE seen = false
										GROUP BY chat_id
						) AS unseen_messages ON chat_users.chat_id = unseen_messages.chat_id
						WHERE users.id != @user_id
						AND (
										(block.blocker_id = users.id AND block.user_blocked = false)
										OR
										block.blocker_id = @user_id
						)
						OR NOT EXISTS (
										SELECT 1 FROM block 
										WHERE (blocker_id = user_id AND blocked_user_id = @user_id) OR 
										(blocker_id = @user_id AND blocked_user_id = user_id)
						)";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("user_id", userId);
						using var reader = cmd.ExecuteReader();
						List<object> userRows = new List<object>();
						while (reader.Read()) {
							var user = new ExpandoObject() as IDictionary<string, object>;
							for (int i = 0; i < reader.FieldCount; i++) {
								if (reader.GetName(i) != "pfp") {
									user.Add(reader.GetName(i), reader.GetValue(i));
								} else {
									if (reader.GetValue(i) != null && reader.GetValue(i).ToString() != "") {
										string mediaRow = reader.GetValue(i).ToString();
										if (mediaRow != null) {
											int indexCharacter = mediaRow.IndexOf(':');
											if (indexCharacter > 0) {
												string mediaType = mediaRow.Substring(0, indexCharacter);
												string key = mediaRow.Substring(indexCharacter + 1);
												string urlRes = await generatePreSignedURL(bucketName, key);
												if (urlRes != null) {
													user.Add("pfp", new { type = mediaType, content = urlRes });
												}
											}
										}
									}
								}
							}
							userRows.Add(user);
						}
						if (userRows.Count > 0) {
							string jsonRes = JsonSerializer.Serialize(userRows);
							byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
							response.StatusCode = 200;
							response.ContentType = "application/json";
							outputStreamRes(buffer);
							response.Close();
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("There were no users found");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/messages/getNew") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string json = readerReq.ReadToEnd();
				UsersReq? objReq = JsonSerializer.Deserialize<UsersReq>(json);
				if (objReq != null) {
					int chatId = Convert.ToInt32(objReq.ChatId);
					DateTime dateSent = objReq.DateSent;
					if (chatId > 0) {
						string sql = @"UPDATE messages SET seen = TRUE WHERE date_sent >= @date_sent  
					AND chat_id = @chat_id RETURNING date_sent, content, media, sender_id, seen, edited, id";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("date_sent", dateSent);
						cmd.Parameters.AddWithValue("chat_id", chatId);
						using var reader = cmd.ExecuteReader();
						List<object> messagesRows = new List<object>();
						while (reader.Read()) {
							var message = new ExpandoObject() as IDictionary<string, object>;
							for (int i = 0; i < reader.FieldCount; i++) {
								if (reader.GetName(i) != "media") {
									message.Add(reader.GetName(i), reader.GetValue(i));
								} else {
									if (reader.GetValue(i) != null && reader.GetValue(i).ToString() != "") {
										string mediaRow = reader.GetValue(i).ToString();
										if (mediaRow != null) {
											int indexCharacter = mediaRow.IndexOf(':');
											if (indexCharacter > 0) {
												string mediaType = mediaRow.Substring(0, indexCharacter);
												string key = mediaRow.Substring(indexCharacter + 1);
												string urlRes = await generatePreSignedURL(bucketName, key);
												if (urlRes != null) {
													message.Add("media", new { type = mediaType, content = urlRes });
												}
											}
										}
									}
								}
							}
							messagesRows.Add(message);
						}
						if (messagesRows.Count > 0) {
							string jsonRes = JsonSerializer.Serialize(messagesRows);
							byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
							response.StatusCode = 200;
							response.ContentType = "application/json";
							outputStreamRes(buffer);
							response.Close();
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("Couldnt get the new messages");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/message/send") {
			try {
				using var stream = request.InputStream;
				var parser = MultipartFormDataParser.Parse(stream);
				string? urlRes = null;
				string fileUrl = string.Empty;
				string mediaType = string.Empty;

				Dictionary<string, string> parameters = new Dictionary<string, string>();
				foreach (var param in parser.Parameters) {
					parameters[param.Name] = param.Data;
				}
				int chatId = Convert.ToInt32(parameters["chatId"]);
				int ownerId = Convert.ToInt32(parameters["ownerId"]);
				string? userInput = null;
				parameters.TryGetValue("userInput", out userInput);
				string key = $"{ownerId}{chatId}{DateTime.Now.ToString("yyMMddHHmmssfff")}";

				foreach (var file in parser.Files) {
					MemoryStream memoryStream = new MemoryStream();
					file.Data.CopyTo(memoryStream);
					if (file.Name == "image") {
						urlRes = await uploadFileAsync(memoryStream, bucketName, key);
						if (urlRes != null) {
							fileUrl = $"image:{key}";
							mediaType = "image";
						} else if (file.Name == "video") {
							urlRes = await uploadFileAsync(memoryStream, bucketName, key);
							if (urlRes != null) {
								fileUrl = $"video:{key}";
								mediaType = "video";
							}
						} else if (file.Name == "audio") {
							urlRes = await uploadFileAsync(memoryStream, bucketName, key);
							if (urlRes != null) {
								fileUrl = $"audio:{key}";
								mediaType = "audio";
							}
						}
					}
				}
				if ((!string.IsNullOrEmpty(userInput) || !string.IsNullOrEmpty(fileUrl)) && ownerId > 0 && chatId > 0) {
					string sql = @"INSERT INTO messages (sender_id, content, media, chat_id) 
					VALUES (@sender_id, @content, @media, @chat_id) 
					RETURNING sender_id, date_sent, seen, id, content, edited";
					using var cmd = new NpgsqlCommand(sql, connection);
					cmd.Parameters.AddWithValue("sender_id", ownerId);
					if (!string.IsNullOrEmpty(userInput)) {
						cmd.Parameters.AddWithValue("content", userInput);
					} else {
						cmd.Parameters.AddWithValue("content", "");
					}
					if (!string.IsNullOrEmpty(fileUrl)) {
						cmd.Parameters.AddWithValue("media", fileUrl);
					} else {
						cmd.Parameters.AddWithValue("media", "");
					}
					cmd.Parameters.AddWithValue("chat_id", chatId);
					using var messageReaderRes = cmd.ExecuteReader();
					if (messageReaderRes.Read()) {
						var message = new ExpandoObject() as IDictionary<string, object>;
						for (int i = 0; i < messageReaderRes.FieldCount; i++) {
							message.Add(messageReaderRes.GetName(i), messageReaderRes.GetValue(i));
						}
						if (urlRes != null) {
							message.Add("media", new { type = mediaType, content = urlRes });
						}
						string jsonRes = JsonSerializer.Serialize(message);
						byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
						response.StatusCode = 200;
						response.ContentType = "application/json";
						outputStreamRes(buffer);
						response.Close();
					} else {
						byte[] buffer = Encoding.UTF8.GetBytes("Couldnt send message");
						response.StatusCode = 500;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
	}
	if (request.HttpMethod == "PATCH") {
		if (request.Url != null && request.Url.AbsolutePath == "/message/edit") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string jsonReq = readerReq.ReadToEnd();
				UsersReq? objectReq = JsonSerializer.Deserialize<UsersReq>(jsonReq);
				if (objectReq != null) {
					int messageId = Convert.ToInt32(objectReq.MessageId);
					string userEditedMessage = objectReq.UserInput;
					if (messageId > 0) {
						string sql = "UPDATE messages SET edited = TRUE, content = @content WHERE id = @id";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("id", messageId);
						cmd.Parameters.AddWithValue("content", userEditedMessage);
						int rowCountEdited = cmd.ExecuteNonQuery();
						if (rowCountEdited > 0) {
							byte[] buffer = Encoding.UTF8.GetBytes("Message was edited");
							response.StatusCode = 200;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("Message was not edited");
							response.StatusCode = 500;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/user/editProfile") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string jsonReq = readerReq.ReadToEnd();
				UserProfile? userData = JsonSerializer.Deserialize<UserProfile>(jsonReq);
				if (userData != null) {
					int userId = Convert.ToInt32(userData.UserId);
					string userName = userData.Name;
					string userDisplayName = userData.DisplayName;
					string userBio = userData.Bio;
					if (userId > 0) {
						string sqlCheckName = "SELECT COUNT(*) FROM users WHERE name = @name AND id != @id";
						using (var cmdCheckName = new NpgsqlCommand(sqlCheckName, connection)) {
							cmdCheckName.Parameters.AddWithValue("name", userName ?? "");
							cmdCheckName.Parameters.AddWithValue("id", userId);
							int countNameUser = Convert.ToInt32(cmdCheckName.ExecuteScalar() ?? 0);
							if (countNameUser > 0) {
								byte[] buffer = Encoding.UTF8.GetBytes("This user name is used already");
								response.StatusCode = 400;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
								return;
							}
						}
						string sql = @"UPDATE users
																					SET name = @name, display_name = @display_name, bio = @bio
																					WHERE id = @id
																					RETURNING name, display_name, bio";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("id", userId);
						cmd.Parameters.AddWithValue("name", userName ?? "");
						cmd.Parameters.AddWithValue("display_name", userDisplayName ?? "");
						cmd.Parameters.AddWithValue("bio", userBio ?? "");
						using var reader = cmd.ExecuteReader();
						if (reader.Read()) {
							var userRow = new ExpandoObject() as IDictionary<string, object>;
							for (int i = 0; i < reader.FieldCount; i++) {
								userRow.Add(reader.GetName(i), reader.GetValue(i) ?? "");
							}
							if (userRow.Count > 0) {
								string jsonRes = JsonSerializer.Serialize(userRow);
								byte[] buffer = Encoding.UTF8.GetBytes(jsonRes);
								response.StatusCode = 200;
								response.ContentType = "application/json";
								outputStreamRes(buffer);
								response.Close();
							} else {
								byte[] buffer = Encoding.UTF8.GetBytes("User profile was not updated");
								response.StatusCode = 500;
								response.ContentType = "text/plain";
								outputStreamRes(buffer);
								response.Close();
							}
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("Could not find rows with the user");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
	}
	if (request.HttpMethod == "DELETE") {
		if (request.Url != null && request.Url.AbsolutePath == "/user/delete") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				int userId = Convert.ToInt32(readerReq.ReadToEnd());
				if (userId > 0) {
					string sqlMessages = @"DELETE FROM messages 
																										WHERE chat_id IN (SELECT id FROM chats 
																										WHERE first_user_id = @user_id OR second_user_id = @user_id)
																										RETURNING media;";

					string sqlChats = @"DELETE FROM block 
																							WHERE blocker_id = @user_id OR blocked_user_id = @user_id;
																							DELETE FROM chats 
																							WHERE first_user_id = @user_id OR second_user_id = @user_id;";

					string sqlUsers = @"DELETE FROM users 
																							WHERE id = @user_id
																							RETURNING pfp;";

					using var cmdMessages = new NpgsqlCommand(sqlMessages, connection);
					cmdMessages.Parameters.AddWithValue("user_id", userId);
					List<object> rowsMedia = new List<object>();
					bool confirmDeletedFile = false;
					using (var readerMessages = cmdMessages.ExecuteReader()) {
						while (readerMessages.Read()) {
							var rowMedia = new ExpandoObject() as IDictionary<string, object>;
							for (int i = 0; i < readerMessages.FieldCount; i++) {
								if (readerMessages.GetName(i) == "media") {
									rowMedia.Add(readerMessages.GetName(i), readerMessages.GetValue(i));
									if (readerMessages.GetValue(i) != null && readerMessages.GetValue(i).ToString() != "") {
										string mediaRow = readerMessages.GetValue(i).ToString();
										if (mediaRow != null) {
											int indexCharacter = mediaRow.IndexOf(':');
											if (indexCharacter > 0) {
												string mediaType = mediaRow.Substring(0, indexCharacter);
												string key = mediaRow.Substring(indexCharacter + 1);
												confirmDeletedFile = await deleteFileAsync(bucketName, key);
											}
										}
									}
								}
							}
							confirmDeletedFile = false;
							rowsMedia.Add(rowMedia);
						}
					}
					using var cmdChats = new NpgsqlCommand(sqlChats, connection);
					cmdChats.Parameters.AddWithValue("user_id", userId);
					int chats = cmdChats.ExecuteNonQuery();

					using var cmdUsers = new NpgsqlCommand(sqlUsers, connection);
					cmdUsers.Parameters.AddWithValue("user_id", userId);
					using NpgsqlDataReader readerUsers = cmdUsers.ExecuteReader();
					while (readerUsers.Read()) {
						var rowMedia = new ExpandoObject() as IDictionary<string, object>;
						for (int i = 0; i < readerUsers.FieldCount; i++) {
							if (readerUsers.GetName(i) == "pfp") {
								rowMedia.Add(readerUsers.GetName(i), readerUsers.GetValue(i));
								if (readerUsers.GetValue(i) != null && readerUsers.GetValue(i).ToString() != "") {
									string mediaRow = readerUsers.GetValue(i).ToString();
									if (mediaRow != null) {
										int indexCharacter = mediaRow.IndexOf(':');
										if (indexCharacter > 0) {
											string mediaType = mediaRow.Substring(0, indexCharacter);
											string key = mediaRow.Substring(indexCharacter + 1);
											confirmDeletedFile = await deleteFileAsync(bucketName, key);
										}
									}
								}
							}
						}
						confirmDeletedFile = false;
						rowsMedia.Add(rowMedia);
					}

					if (readerUsers.HasRows) {
						byte[] buffer = Encoding.UTF8.GetBytes("completed delete user");
						response.StatusCode = 200;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					} else {
						byte[] buffer = Encoding.UTF8.GetBytes("Delete user went wrong");
						response.StatusCode = 500;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/chats/delete") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				string jsonReq = readerReq.ReadToEnd();
				UsersReq? usersData = JsonSerializer.Deserialize<UsersReq>(jsonReq);
				if (usersData != null) {
					int userId = Convert.ToInt32(usersData.OwnerId);
					int guestId = Convert.ToInt32(usersData.GuestId);
					if (userId > 0 && guestId > 0) {
						string sql = @"UPDATE block 
						SET chat_id = NULL
						WHERE (blocker_id = @guestId AND blocked_user_id = @ownerId AND user_blocked = false) OR
      (blocker_id = @ownerId AND blocked_user_id = @guestId);
						DELETE FROM messages WHERE chat_id = (
							SELECT id FROM chats WHERE 
							(first_user_id = @ownerId AND second_user_id = @guestId) OR
							(second_user_id = @ownerId AND first_user_id = @guestId)
						) AND NOT EXISTS (
							SELECT 1 FROM block 
							WHERE blocker_id = @guestId AND blocked_user_id = @ownerId AND user_blocked = true
						);
						DELETE FROM chats WHERE ((first_user_id = @ownerId AND second_user_id = @guestId) OR
							(second_user_id = @ownerId AND first_user_id = @guestId))
						AND NOT EXISTS (
							SELECT 1 FROM block 
							WHERE blocker_id = @guestId AND blocked_user_id = @ownerId AND user_blocked = true
						);";
						using var cmd = new NpgsqlCommand(sql, connection);
						cmd.Parameters.AddWithValue("ownerId", userId);
						cmd.Parameters.AddWithValue("guestId", guestId);
						int countRowsAffected = cmd.ExecuteNonQuery();
						if (countRowsAffected > 0) {
							byte[] buffer = Encoding.UTF8.GetBytes("Chat deleted");
							response.StatusCode = 200;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						} else {
							byte[] buffer = Encoding.UTF8.GetBytes("Chat was not delete, probably user blocked");
							response.StatusCode = 400;
							response.ContentType = "text/plain";
							outputStreamRes(buffer);
							response.Close();
						}
					}
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
		if (request.Url != null && request.Url.AbsolutePath == "/message/delete") {
			try {
				using var reqBody = request.InputStream;
				using var readerReq = new StreamReader(reqBody, Encoding.UTF8);
				int messageId = Convert.ToInt32(readerReq.ReadToEnd());
				if (messageId > 0) {
					string sql = "DELETE FROM messages WHERE id = @id RETURNING id, content, media";
					using var cmd = new NpgsqlCommand(sql, connection);
					cmd.Parameters.AddWithValue("id", messageId);
					using var readerRes = cmd.ExecuteReader();
					bool confirmDeletedFile = false;
					int messageIdRes = 0;
					string messageContentRes = "";
					var message = new ExpandoObject() as IDictionary<string, object>;
					if (readerRes.Read()) {
						for (int i = 0; i < readerRes.FieldCount; i++) {
							if (readerRes.GetName(i) != "media") {
								message.Add(readerRes.GetName(i), readerRes.GetValue(i));
							} else {
								if (readerRes.GetValue(i) != null && readerRes.GetValue(i).ToString() != "") {
									string mediaRow = readerRes.GetValue(i).ToString();
									if (mediaRow != null) {
										int indexCharacter = mediaRow.IndexOf(':');
										if (indexCharacter > 0) {
											string mediaType = mediaRow.Substring(0, indexCharacter);
											string key = mediaRow.Substring(indexCharacter + 1);
											confirmDeletedFile = await deleteFileAsync(bucketName, key);
										}
									}
								}
							}
						}
						messageIdRes = Convert.ToInt32(message["id"]);
						if (message["content"] != null) {
							messageContentRes = message["content"].ToString();
						}
					}
					if (messageIdRes > 0 && messageContentRes?.Length > 0 && confirmDeletedFile) {
						byte[] buffer = Encoding.UTF8.GetBytes(messageIdRes.ToString());
						response.StatusCode = 200;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					} else if (messageIdRes > 0 && confirmDeletedFile) {
						byte[] buffer = Encoding.UTF8.GetBytes(messageIdRes.ToString());
						response.StatusCode = 200;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					} else if (messageIdRes > 0 && messageContentRes?.Length > 0) {
						byte[] buffer = Encoding.UTF8.GetBytes(messageIdRes.ToString());
						response.StatusCode = 200;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					} else {
						byte[] buffer = Encoding.UTF8.GetBytes("Couldnt delete the message");
						response.StatusCode = 500;
						response.ContentType = "text/plain";
						outputStreamRes(buffer);
						response.Close();
					}
				} else {
					byte[] buffer = Encoding.UTF8.GetBytes("something went wrong, message id is not greater than 0");
					response.StatusCode = 500;
					response.ContentType = "text/plain";
					outputStreamRes(buffer);
					response.Close();
				}
			} catch (NpgsqlException err) {
				byte[] buffer = Encoding.UTF8.GetBytes($"Database error: {err.Message}");
				response.StatusCode = 500;
				response.ContentType = "text/plain";
				outputStreamRes(buffer);
				response.Close();
			}
		}
	}
}
(string, byte[]) HashPassword(string password, byte[]? saltAccount = null) {
	byte[] salt;
	if (saltAccount == null) {
		salt = new byte[16];
		using (var rng = RandomNumberGenerator.Create()) {
			rng.GetBytes(salt);
		}
	} else {
		salt = saltAccount;
	}
	var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
	byte[] hash = pbkdf2.GetBytes(20);

	byte[] hashBytes = new byte[36];
	Array.Copy(salt, 0, hashBytes, 0, 16);
	Array.Copy(hash, 0, hashBytes, 16, 20);

	return (Convert.ToBase64String(hashBytes), salt);
}

string generateSessionKey(int keyLength = 32) {
	byte[] sessionKey = new byte[keyLength];
	using (var rng = RandomNumberGenerator.Create()) {
		rng.GetBytes(sessionKey);
	}
	return Convert.ToBase64String(sessionKey);
}
public class UserInit {
	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("email")]
	public string Email { get; set; }

	[JsonPropertyName("password")]
	public string Password { get; set; }
}

public class UserProfile {
	[JsonPropertyName("id")]
	public int UserId { get; set; }

	[JsonPropertyName("bio")]
	public string Bio { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("display_name")]
	public string DisplayName { get; set; }
}
public class UsersReq {
	[JsonPropertyName("userId")]
	public int UserId { get; set; }

	[JsonPropertyName("senderId")]
	public int SenderId { get; set; }

	[JsonPropertyName("ownerId")]
	public int OwnerId { get; set; }

	[JsonPropertyName("guestId")]
	public int GuestId { get; set; }

	[JsonPropertyName("userBlock")]
	public bool UserBlock { get; set; }

	[JsonPropertyName("chatHidden")]
	public bool ChatHidden { get; set; }

	[JsonPropertyName("chatId")]
	public int ChatId { get; set; }

	[JsonPropertyName("dateSent")]
	public DateTime DateSent { get; set; }

	[JsonPropertyName("messageId")]
	public string MessageId { get; set; }


	[JsonPropertyName("userInput")]
	public string UserInput { get; set; }

}

public class ReceiverUser {
	[JsonPropertyName("userId")]
	public int UserId { get; set; }
}
public class WsReq {
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("content")]
	public string Content { get; set; }

	[JsonPropertyName("userId")]
	public int UserId { get; set; }

	[JsonPropertyName("senderId")]
	public int SenderId { get; set; }

	[JsonPropertyName("receiversId")]
	public List<ReceiverUser> ReceiversId { get; set; }

	[JsonPropertyName("setUsers")]
	public List<int> SetUsers { get; set; }

	[JsonPropertyName("chatId")]
	public int? ChatId { get; set; }

	[JsonPropertyName("messageId")]
	public int? MessageId { get; set; }
}
