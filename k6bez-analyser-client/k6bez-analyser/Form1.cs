using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace k6bez_analyser
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        bool stopping;
        private void button1_Click(object sender, EventArgs e)
        {
            if (button1.Text == "Stop")
            {
                stopping = true;
                return;
            }

            double start, end;

            if (!double.TryParse(startF.Text, out start))
            {
                MessageBox.Show("Invalid start frequency");
                return;
            }

            if (!double.TryParse(endF.Text, out end))
            {
                MessageBox.Show("Invalid end frequency");
                return;
            }

            if (start <= 0 || start > 30)
            {
                MessageBox.Show("Start frequency out of range");
                return;
            }

            if (end <= 0 || end > 30)
            {
                MessageBox.Show("End frequency out of range");
                return;
            }

            if (end < start)
            {
                MessageBox.Show("End frequency must be higher than start");
                return;
            }

            DisableControls();

            if (continuous.Checked)
            {
                button1.Text = "Stop";
            }
            else
            {
                button1.Enabled = false;
            }

            var bw = new BackgroundWorker();
            bw.DoWork += Bw_DoWork;
            bw.RunWorkerCompleted += Bw_RunWorkerCompleted;
            bw.WorkerReportsProgress = true;
            bw.ProgressChanged += Bw_ProgressChanged;
            bw.RunWorkerAsync(new WorkArg { Start = start, End = end, Continuous = continuous.Checked, Steps = 50 });
        }

        private void Bw_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            var results = (ScanResult[])e.UserState;

            chart1.Series[0].Points.Clear();
            chart1.ChartAreas[0].AxisX.Minimum = results.Min(r => r.FreqHz) / 1000000.0;
            chart1.ChartAreas[0].AxisX.Maximum = results.Max(r => r.FreqHz) / 1000000.0;
            chart1.ChartAreas[0].AxisX.Title = "F (MHz)";

            double rangeMHz = (results.Last().FreqHz - results[0].FreqHz) / 1000000.0;

            if (rangeMHz < 0.1)
            {
                chart1.ChartAreas[0].AxisX.Interval = 0.01;
            }
            else if (rangeMHz < 3)
            {
                chart1.ChartAreas[0].AxisX.Interval = 0.1;
            }
            else
            {
                chart1.ChartAreas[0].AxisX.Interval = 1;
            }

            chart1.ChartAreas[0].AxisX.IntervalAutoMode = IntervalAutoMode.FixedCount;
            chart1.ChartAreas[0].AxisY.Minimum = 0;
            chart1.ChartAreas[0].AxisY.Maximum = ((int)results.Max(r => r.Swr) * 1.2) + 1;
            chart1.ChartAreas[0].AxisY.Interval = 1;
            chart1.ChartAreas[0].AxisY.Title = "SWR";
            foreach (var item in results)
            {
                chart1.Series[0].Points.Add(new DataPoint(item.FreqHz / 1000000.0, item.Swr));
            }

            double lowestSwr = results.Min(r => r.Swr);
            var lowestSwrFreqs = new List<int>();
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i].Swr > lowestSwr)
                {
                    if (lowestSwrFreqs.Any())
                        break;
                    else
                        continue;
                }

                lowestSwrFreqs.Add(results[i].FreqHz);
            }

            bestSwr.Text = string.Format("{0:0.00}:1", Math.Round(lowestSwr, 2));
            bestSwrFreq.Text = string.Format("{0:0.000}MHz", lowestSwrFreqs.Average()/1000000);
            swrPanel.Visible = true;
        }

        private void Bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            button1.Text = "Sweep";
            button1.Enabled = true;
            stopping = false;
            EnableControls();
        }

        class WorkArg
        {
            public double Start { get; set; }
            public double End { get; set; }
            public int Steps { get; set; }
            public bool Continuous { get; set; }
        }

        private void Bw_DoWork(object sender, DoWorkEventArgs e)
        {
            WorkArg arg = (WorkArg)e.Argument;

            ScanResult[] results;
            using (var analyser = new Analyser(SerialPort.GetPortNames().First()))
            {
                do
                {
                    analyser.StartMhz = arg.Start;
                    analyser.EndMhz = arg.End;
                    analyser.Steps = arg.Steps;
                    results = analyser.Scan();

                    ((BackgroundWorker)sender).ReportProgress(0, results);

                } while (arg.Continuous && !stopping);
            }
        }

        private void EnableControls()
        {
            startF.Enabled = endF.Enabled = continuous.Enabled = true;
        }

        private void DisableControls()
        {
            startF.Enabled = endF.Enabled = continuous.Enabled = false;
        }
    }

    class ScanResult
    {
        public int FreqHz { get; set; }
        public double Swr { get; set; }
        public int Fwd { get; set; }
        public int Rev { get; set; }

        public override string ToString()
        {
            return string.Format("{0:0.000} MHz: {1:0.00}:1", FreqHz / 1000000.0, Swr);
        }
    }

    class Analyser : IDisposable
    {
        SerialPort sp;

        public Analyser(string portName)
        {
            sp = new SerialPort(portName);
            sp.BaudRate = 57600;
            sp.Open();
            UpdateStatus();
            Idle = true;
        }

        public ScanResult[] Scan()
        {
            sp.Write(string.Format("{0:0}", StartMhz * 1000000));
            sp.Write("\rA\r");

            sp.Write(string.Format("{0:0}", EndMhz * 1000000));
            sp.Write("\rB\r");

            sp.Write(string.Format("{0:0}", Steps));
            sp.Write("\rN\r");

            sp.Write("\rS\r");

            var results = new List<ScanResult>();
            var sb = new StringBuilder();
            string resultLine;
            while (true)
            {
                resultLine = sp.ReadLine();

                if (resultLine.Trim() == "End")
                {
                    break;
                }

                sb.AppendLine(resultLine);
                string interim = sb.ToString();

                results.Add(new ScanResult
                {
                    FreqHz = (int)double.Parse(resultLine.SplitElement(",", 0)),
                    Swr = double.Parse(resultLine.SplitElement(",", 1)) / 1000.0,
                    Fwd = (int)double.Parse(resultLine.SplitElement(",", 2)),
                    Rev = (int)double.Parse(resultLine.SplitElement(",", 3)),
                });
            }

            return results.ToArray();
        }

        public bool Idle { get; set; }

        public double StartMhz { get; set; }
        public double EndMhz { get; set; }
        public int Steps { get; set; }

        void UpdateStatus()
        {
            sp.ReadTimeout = 1000;
            while (sp.BytesToRead == 0)
            {
                sp.Write("?");
                Thread.Sleep(500);
            }

            sp.ReadTimeout = -1;

            /*
             * Start Freq:1.00
               Stop Freq:22.00
               Num Steps:500
             */

            string line1 = sp.ReadLine();
            string line2 = sp.ReadLine();
            string line3 = sp.ReadLine();

            StartMhz = double.Parse(line1.SplitElement(":", 1));
            EndMhz = double.Parse(line2.SplitElement(":", 1));
            Steps = int.Parse(line3.SplitElement(":", 1));
        }

        public void Dispose()
        {
            sp.Close();
            sp.Dispose();
        }
    }

    static class Extensions
    {
        public static string SplitElement(this string input, string splitOn, int elementIndex)
        {
            return input.Split(new[] { splitOn }, StringSplitOptions.RemoveEmptyEntries)[elementIndex];
        }
    }
}
