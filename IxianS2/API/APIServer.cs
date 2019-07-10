using IXICore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace S2
{
    class APIServer : GenericAPIServer
    {
        public APIServer(List<string> listen_URLs, Dictionary<string, string> authorized_users = null, List<string> allowed_IPs = null)
        {
            // Start the API server
            start(listen_URLs, authorized_users, allowed_IPs);
        }

        protected override bool processRequest(HttpListenerContext context, string methodName, Dictionary<string, object> parameters)
        {
            JsonResponse response = null;

            if (methodName.Equals("testadd", StringComparison.OrdinalIgnoreCase))
            {
                response = onTestAdd(parameters);
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

        public JsonResponse onTestAdd(Dictionary<string, object> parameters)
        {
            if (!parameters.ContainsKey("wallet"))
            {
                JsonError error = new JsonError { code = (int)RPCErrorCode.RPC_INVALID_PARAMETER, message = "Parameter 'wallet' is missing" };
                return new JsonResponse { result = null, error = error };
            }

            byte[] wallet = Base58Check.Base58CheckEncoding.DecodePlain((string)parameters["wallet"]);

            string responseString = JsonConvert.SerializeObject("Friend added successfully");

            if (TestClientNode.addFriend(wallet) == false)
            {
                responseString = JsonConvert.SerializeObject("Could not find wallet id or add friend");
            }

            return new JsonResponse() { result = responseString, error = null };
        }
    }
}
