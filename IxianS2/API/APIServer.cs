﻿using DLT.Meta;
using IXICore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace S2
{
    class APIServer : GenericAPIServer
    {
        public APIServer()
        {
            // Start the API server
            start(String.Format("http://localhost:{0}/", Config.apiPort));
        }

        protected override bool processRequest(HttpListenerContext context, string methodName, Dictionary<string, object> parameters)
        {
            JsonResponse response = null;

            if (methodName.Equals("testadd", StringComparison.OrdinalIgnoreCase))
            {
                byte[] wallet = Base58Check.Base58CheckEncoding.DecodePlain((string)parameters["wallet"]);

                string responseString = JsonConvert.SerializeObject("Friend added successfully");

                if (TestClientNode.addFriend(wallet) == false)
                {
                    responseString = JsonConvert.SerializeObject("Could not find wallet id or add friend");
                }

                response = new JsonResponse() { result = responseString };
                
            }

            if (response == null)
            {
                return false;
            }

            // Set the content type to plain to prevent xml parsing errors in various browsers
            context.Response.ContentType = "application/json";

            sendResponse(context.Response, response);

            context.Response.Close();

            return true;
        }
    }
}