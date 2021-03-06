using MuMech;
using System.Linq;

namespace MechJebRPM
{
	public class MechJebRPMVariables: PartModule
	{
		private MechJebCore activeJeb;

		public object ProcessVariable(string variable)
		{
			activeJeb = vessel.GetMasterMechJeb();
			switch (variable) {
				case "MECHJEBAVAILABLE":
					if (activeJeb != null)
						return 1;
					return -1;
				case "DELTAV":
					if (activeJeb != null) {
						MechJebModuleStageStats stats = activeJeb.GetComputerModule<MechJebModuleStageStats>();
						stats.RequestUpdate(this);
						return stats.vacStats.Sum(s => s.deltaV);
					}
					return null;
				case "DELTAVSTAGE":
					if (activeJeb != null) {
						MechJebModuleStageStats stats = activeJeb.GetComputerModule<MechJebModuleStageStats>();
						stats.RequestUpdate(this);
						return stats.vacStats[stats.vacStats.Length - 1].deltaV;
					}
					return null;
				case "PREDICTEDLANDINGERROR":
					// If there's a failure at any step, exit with a -1.
					// The landing prediction system can be costly, and it
					// expects someone to be registered with it for it to run.
					// So, we'll have a MechJebRPM button to enable landing
					// predictions.  And maybe add it in to the MJ menu.
					if (activeJeb != null && activeJeb.target.PositionTargetExists == true) {
						var predictor = activeJeb.GetComputerModule<MechJebModuleLandingPredictions>();
						if (predictor != null && predictor.enabled) {
							ReentrySimulation.Result result = predictor.GetResult();
							if (result != null && result.outcome == ReentrySimulation.Outcome.LANDED) {
								// We're going to hit something!
								double error = Vector3d.Distance(vessel.mainBody.GetRelSurfacePosition(result.endPosition.latitude, result.endPosition.longitude, 0),
																	vessel.mainBody.GetRelSurfacePosition(activeJeb.target.targetLatitude, activeJeb.target.targetLongitude, 0));
								return error;
							}
						}
					}
					return -1;
			}
			return null;
		}
	}
}

