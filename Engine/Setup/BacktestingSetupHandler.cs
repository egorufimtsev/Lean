﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

/**********************************************************
* USING NAMESPACES
**********************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm;
using QuantConnect.AlgorithmFactory;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.Setup
{
    /// <summary>
    /// Backtesting setup handler processes the algorithm initialize method and sets up the internal state of the algorithm class.
    /// </summary>
    public class BacktestingSetupHandler : ISetupHandler
    {
        /******************************************************** 
        * PRIVATE VARIABLES
        *********************************************************/
        private TimeSpan _maxRuntime = TimeSpan.FromSeconds(300);
        private decimal _startingCaptial = 0;
        private int _maxOrders = 0;
        private DateTime _startingDate = new DateTime(1998, 01, 01);

        /******************************************************** 
        * PUBLIC PROPERTIES
        *********************************************************/
        /// <summary>
        /// Internal errors list from running the setup proceedures.
        /// </summary>
        public List<string> Errors
        { 
            get; 
            set; 
        }

        /// <summary>
        /// Maximum runtime of the algorithm in seconds.
        /// </summary>
        /// <remarks>Maximum runtime is a formula based on the number and resolution of symbols requested, and the days backtesting</remarks>
        public TimeSpan MaximumRuntime
        {
            get 
            {
                return _maxRuntime;
            }
        }


        /// <summary>
        /// Starting capital according to the users initialize routine.
        /// </summary>
        /// <remarks>Set from the user code.</remarks>
        /// <seealso cref="QCAlgorithm.SetCash(decimal)"/>
        public decimal StartingCapital
        {
            get 
            {
                return _startingCaptial;
            }
        }

        /// <summary>
        /// Start date for analysis loops to search for data.
        /// </summary>
        /// <seealso cref="QCAlgorithm.SetStartDate(DateTime)"/>
        public DateTime StartingDate
        {
            get
            {
                return _startingDate;
            }
        }

        /// <summary>
        /// Maximum number of orders for this backtest.
        /// </summary>
        /// <remarks>To stop algorithm flooding the backtesting system with hundreds of megabytes of order data we limit it to 100 per day</remarks>
        public int MaxOrders
        {
            get 
            {
                return _maxOrders;
            }
        }

        /******************************************************** 
        * PUBLIC CONSTRUCTOR
        *********************************************************/
        /// <summary>
        /// Initialize the backtest setup handler.
        /// </summary>
        public BacktestingSetupHandler() 
        {
            Errors = new List<string>();
        }

        /******************************************************** 
        * PUBLIC METHODS
        *********************************************************/
        /// <summary>
        /// Creates a new algorithm instance. Verified there's only one defined in the assembly and requires
        /// instantiation to take less than 10 seconds
        /// </summary>
        /// <param name="assemblyPath">Physical location of the assembly.</param>
        /// <returns>Algorithm instance.</returns>
        public IAlgorithm CreateAlgorithmInstance(string assemblyPath)
        {
            string error;
            IAlgorithm algorithm;

            // limit load times to 10 seconds and force the assembly to have exactly one derived type
            var loader = new Loader(TimeSpan.FromSeconds(10), names => names.SingleOrDefault());
            bool complete = loader.TryCreateAlgorithmInstanceWithIsolator(assemblyPath, out algorithm, out error);
            if (!complete) throw new Exception(error + " Try re-building algorithm.");

            return algorithm;
        }

        /// <summary>
        /// Setup the algorithm cash, dates and data subscriptions as desired.
        /// </summary>
        /// <param name="algorithm">Algorithm instance</param>
        /// <param name="brokerage">Brokerage instance</param>
        /// <param name="baseJob">Algorithm job</param>
        /// <returns>Boolean true on successfully initializing the algorithm</returns>
        public bool Setup(IAlgorithm algorithm, out IBrokerage brokerage, AlgorithmNodePacket baseJob)
        {
            var job = baseJob as BacktestNodePacket;
            if (job == null)
            {
                throw new ArgumentException("Expected BacktestNodePacket but received " + baseJob.GetType().Name);
            }

            // Must be set since its defined as an out parameters
            brokerage = new BacktestingBrokerage(algorithm);

            if (algorithm == null)
            {
                Errors.Add("Could not create instance of algorithm");
                return false;
            }

            //Make sure the algorithm start date ok.
            if (job.PeriodStart == default(DateTime))
            {
                Errors.Add("Algorithm start date was never set");
                return false;
            }
            
            //Execute the initialize code:
            var initializeComplete = Isolator.ExecuteWithTimeLimit(TimeSpan.FromSeconds(10), () =>
            {
                try
                {
                    //0.0 Set the algorithm time before we even initialize:
                    algorithm.SetDateTime(job.PeriodStart);
                    //1.0 Initialise the algorithm, get the required data:
                    algorithm.Initialize();
                    //1.2 Set the algorithm to locked to avoid messing with cash:
                    algorithm.SetLocked();
                }
                catch (Exception err)
                {
                    Errors.Add("Failed to initialize algorithm: Initialize(): " + err.Message);
                }
            });

            //Before continuing, detect if this is ready:
            if (!initializeComplete) return false;

            //Calculate the max runtime for the strategy
            _maxRuntime = GetMaximumRuntime(job.PeriodStart, job.PeriodFinish, algorithm.SubscriptionManager.Count);

            //Get starting capital:
            _startingCaptial = algorithm.Portfolio.Cash;

            //Max Orders: 100 per day:
            _maxOrders = (int)(job.PeriodFinish - job.PeriodStart).TotalDays * 100;

            //Starting date of the algorithm:
            _startingDate = job.PeriodStart;

            //Put into log for debugging:
            Log.Trace("SetUp Backtesting: User: " + job.UserId + " ProjectId: " + job.ProjectId + " AlgoId: " + job.AlgorithmId);
            Log.Trace("Dates: Start: " + job.PeriodStart.ToShortDateString() + " End: " + job.PeriodFinish.ToShortDateString() + " Cash: " + _startingCaptial.ToString("C"));

            if (Errors.Count > 0)
            {
                initializeComplete = false;
            }
            return initializeComplete;
        }


        /// <summary>
        /// Calculate the maximum runtime for this algorithm job.
        /// </summary>
        /// <param name="start">State date of the algorithm</param>
        /// <param name="finish">End date of the algorithm</param>
        /// <param name="subscriptionCount">Number of data feeds the user has requested</param>
        /// <returns>Timespan maximum run period</returns>
        private TimeSpan GetMaximumRuntime(DateTime start, DateTime finish, int subscriptionCount)
        {
            double maxRunTime = 0;
            var jobDays = (finish - start).TotalDays;

            maxRunTime = 10 * subscriptionCount * jobDays;

            //Rationalize:
            if ((maxRunTime / 3600) > 12)
            {
                //12 hours maximum
                maxRunTime = 3600 * 12;
            }
            else if (maxRunTime < 60)
            {
                //If less than 60 seconds.
                maxRunTime = 60;
            }

            Log.Trace("BacktestingSetupHandler.GetMaxRunTime(): Job Days: " + jobDays + " Max Runtime: " + Math.Round(maxRunTime / 60) + " min");

            //Override for windows:
            if (OS.IsWindows)
            {
                maxRunTime = 24 * 60 * 60;
            }

            return TimeSpan.FromSeconds(maxRunTime);
        }

        /// <summary>
        /// Setup error handlers for the backtest.
        /// </summary>
        /// <param name="results">Result handler</param>
        /// <param name="brokerage">Brokerage interface</param>
        /// <returns>Boolean true on successful setup</returns>
        /// <remarks>Not used in a backtesting setup handler. This is primarily for setting up brokerage error handler functions</remarks>
        public bool SetupErrorHandler(IResultHandler results, IBrokerage brokerage)
        {
            return true;
        }

    } // End Result Handler Thread:

} // End Namespace
