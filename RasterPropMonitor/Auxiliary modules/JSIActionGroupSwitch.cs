using System;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;

namespace JSI
{
	public class JSIActionGroupSwitch: InternalModule
	{
		[KSPField]
		public string animationName = string.Empty;
		[KSPField]
		public bool animateExterior;
		[KSPField]
		public string switchTransform = string.Empty;
		[KSPField]
		public string actionName = "lights";
		[KSPField]
		public string perPodPersistenceName = string.Empty;
		[KSPField]
		public bool reverse;
		[KSPField]
		public float customSpeed = 1f;
		[KSPField]
		public string internalLightName;
		[KSPField]
		public string needsElectricCharge;
		private bool needsElectricChargeValue;
		[KSPField]
		public string switchSound = "Squad/Sounds/sound_click_flick";
		[KSPField]
		public float switchSoundVolume = 0.5f;
		[KSPField]
		public string coloredObject = string.Empty;
		[KSPField]
		public string colorName = "_EmissiveColor";
		[KSPField]
		public string disabledColor = string.Empty;
		private Color disabledColorValue;
		[KSPField]
		public string enabledColor = string.Empty;
		private Color enabledColorValue;
		// Neater.
		private readonly Dictionary<string,KSPActionGroup> groupList = new Dictionary<string,KSPActionGroup> { 
			{ "gear",KSPActionGroup.Gear },
			{ "brakes",KSPActionGroup.Brakes },
			{ "lights",KSPActionGroup.Light },
			{ "rcs",KSPActionGroup.RCS },
			{ "sas",KSPActionGroup.SAS },
			{ "abort",KSPActionGroup.Abort },
			{ "stage",KSPActionGroup.Stage },
			{ "custom01",KSPActionGroup.Custom01 },
			{ "custom02",KSPActionGroup.Custom02 },
			{ "custom03",KSPActionGroup.Custom03 },
			{ "custom04",KSPActionGroup.Custom04 },
			{ "custom05",KSPActionGroup.Custom05 },
			{ "custom06",KSPActionGroup.Custom06 },
			{ "custom07",KSPActionGroup.Custom07 },
			{ "custom08",KSPActionGroup.Custom08 },
			{ "custom09",KSPActionGroup.Custom09 },
			{ "custom10",KSPActionGroup.Custom10 }
		};
		private readonly Dictionary<string,bool> customGroupList = new Dictionary<string,bool> {
			{ "intlight",false },
			{ "dummy",false },
			{ "plugin",false },
		};
		private Animation anim;
		private bool oldState;
		private bool isCustomAction;
		// Persistence for current state variable.
		private PersistenceAccessor persistence;
		private string persistentVarName;
		private Light[] lightObjects;
		private FXGroup audioOutput;
		private const int lightCheckRate = 60;
		private int lightCheckCountdown;
		private RasterPropMonitorComputer comp;
		private bool startupComplete;
		private Renderer colorShiftRenderer;
		private Func<bool> stateHandler;
		private Action<bool> actionHandler;
		private bool isPluginAction;

		private static bool InstantiateHandler(ConfigNode node, InternalModule ourSwitch, out Action<bool> actionCall, out Func<bool> stateCall)
		{
			actionCall = null;
			stateCall = null;
			var handlerConfiguration = new ConfigNode("MODULE");
			node.CopyTo(handlerConfiguration);
			string moduleName = node.GetValue("name");
			string stateMethod = string.Empty;
			if (node.HasValue("stateMethod"))
				stateMethod = node.GetValue("stateMethod");

			InternalModule thatModule = null;
			foreach (InternalModule potentialModule in ourSwitch.internalProp.internalModules)
				if (potentialModule.ClassName == moduleName) {
					thatModule = potentialModule;
					break;
				}

			if (thatModule == null)
				thatModule = ourSwitch.internalProp.AddModule(handlerConfiguration);
			if (thatModule == null)
				return false;
			foreach (MethodInfo m in thatModule.GetType().GetMethods()) {
				if (m.Name == node.GetValue("actionMethod")) {
					actionCall = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), thatModule, m);
				}
				if (!string.IsNullOrEmpty(stateMethod) && m.Name == stateMethod) {
					stateCall = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), thatModule, m);
				}
			}
			return actionCall != null;
		}

		public void Start()
		{
			if (!groupList.ContainsKey(actionName) && !customGroupList.ContainsKey(actionName)) {
				JUtil.LogErrorMessage(this, "Action \"{0}\" is not supported.", actionName);
				return;
			}

			// Parse the needs-electric-charge here.
			if (!string.IsNullOrEmpty(needsElectricCharge)) {
				switch (needsElectricCharge.ToLowerInvariant().Trim()) {
					case "true":
					case "yes":
					case "1":
						needsElectricChargeValue = true;
						break;
					case "false":
					case "no":
					case "0":
						needsElectricChargeValue = false;
						break;
				}
			}

			if (groupList.ContainsKey(actionName)) {
				oldState = vessel.ActionGroups[groupList[actionName]];
			} else {
				isCustomAction = true;
				switch (actionName) {
					case "intlight":
						persistentVarName = internalLightName;
						lightObjects = internalModel.FindModelComponents<Light>();
						needsElectricChargeValue |= string.IsNullOrEmpty(needsElectricCharge) || needsElectricChargeValue;
						break;
					case "plugin":
						persistentVarName = string.Empty;
						foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("PROP")) {
							if (node.GetValue("name") == internalProp.propName) {
								foreach (ConfigNode pluginConfig in node.GetNodes("MODULE")[moduleID].GetNodes("PLUGINACTION")) {
									if (pluginConfig.HasValue("name") && pluginConfig.HasValue("actionMethod")) {
										if (!InstantiateHandler(pluginConfig, this, out actionHandler, out stateHandler)) {
											JUtil.LogErrorMessage(this, "Failed to instantiate action handler {0}", pluginConfig.GetValue("name"));
										} else {
											isPluginAction = true;
											break;
										}
									}
								}
							}
						}
						if (actionHandler == null) {
							actionName = "dummy";
							JUtil.LogMessage(this, "Plugin handlers did not start, reverting to dummy mode.");
						}
						break;
					default:
						persistentVarName = "switch" + internalProp.propID + "_" + moduleID;
						break;
				}
				if (!string.IsNullOrEmpty(perPodPersistenceName)) {
					persistentVarName = perPodPersistenceName;
				}
				persistence = new PersistenceAccessor(part);
			}
			if (needsElectricChargeValue) {
				comp = RasterPropMonitorComputer.Instantiate(internalProp);
				comp.UpdateRefreshRates(lightCheckRate, lightCheckRate);
			}

			// set up the toggle switch
			if (!string.IsNullOrEmpty(switchTransform)) {
				SmarterButton.CreateButton(internalProp, switchTransform, Click);
			}

			if (isCustomAction) {
				if (isPluginAction && stateHandler != null) {
					oldState = stateHandler();
				} else {
					if (!string.IsNullOrEmpty(persistentVarName)) {
						oldState = customGroupList[actionName] = (persistence.GetBool(persistentVarName) ?? oldState);
						if (actionName == "intlight") {
							// We have to restore lighting after reading the
							// persistent variable.
							SetInternalLights(customGroupList[actionName]);
						}
					}
				}
			}

			if (!string.IsNullOrEmpty(animationName)) {
				// Set up the animation
				Animation[] animators = animateExterior ? part.FindModelAnimators(animationName) : internalProp.FindModelAnimators(animationName);
				if (animators.Length > 0) {
					anim = animators[0];
				} else {
					JUtil.LogErrorMessage(this, "Could not find animation \"{0}\" on {2} \"{1}\"", 
						animationName, animateExterior ? part.name : internalProp.name, animateExterior ? "part" : "prop");
					return;
				}
				anim[animationName].wrapMode = WrapMode.Once;

				if (oldState ^ reverse) {
					anim[animationName].speed = float.MaxValue;
					anim[animationName].normalizedTime = 0;

				} else {
					anim[animationName].speed = float.MinValue;
					anim[animationName].normalizedTime = 1;
				}
				anim.Play(animationName);
			} else if (!string.IsNullOrEmpty(coloredObject)) {
				// Set up the color shift.
				colorShiftRenderer = internalProp.FindModelComponent<Renderer>(coloredObject);
				disabledColorValue = ConfigNode.ParseColor32(disabledColor);
				enabledColorValue = ConfigNode.ParseColor32(enabledColor);
				colorShiftRenderer.material.SetColor(colorName, (oldState ^ reverse ? enabledColorValue : disabledColorValue));
			} else
				JUtil.LogMessage(this, "Warning, neither color nor animation are defined.");

			audioOutput = JUtil.SetupIVASound(internalProp, switchSound, switchSoundVolume, false);

			startupComplete = true;
		}

		private void SetInternalLights(bool value)
		{
			foreach (Light lightobject in lightObjects) {
				// I probably shouldn't filter them every time, but I am getting
				// serously confused by this hierarchy.
				if (lightobject.name == internalLightName) {
					lightobject.enabled = value;
				}
			}
		}

		public void Click()
		{
			if (isCustomAction) {
				customGroupList[actionName] = !customGroupList[actionName];
				if (!string.IsNullOrEmpty(persistentVarName))
					persistence.SetVar(persistentVarName, customGroupList[actionName]);
			} else
				vessel.ActionGroups.ToggleGroup(groupList[actionName]);
			// Now we do extra things that with regular actions can't happen.
			switch (actionName) {
				case "intlight":
					SetInternalLights(customGroupList[actionName]);
					break;
				case "plugin":
					actionHandler((stateHandler != null) ? !stateHandler() : customGroupList[actionName]);
					break;
				case "stage":
					if (InputLockManager.IsUnlocked(ControlTypes.STAGING)) {
						Staging.ActivateNextStage();
					}
					break;
			}
		}

		public override void OnUpdate()
		{
			if (!JUtil.IsActiveVessel(vessel))
				return;

			if (!startupComplete)
				return;

			// Bizarre, but looks like I need to animate things offscreen if I want them in the right condition when camera comes back.
			// So there's no check for internal cameras.

			bool state;
			if (isPluginAction && stateHandler != null) {
				state = stateHandler();
			} else if (isCustomAction) {
				if (string.IsNullOrEmpty(switchTransform) && !string.IsNullOrEmpty(perPodPersistenceName)) { 
					// If the switch transform is not given, and the global persistence value is, this means this is a slave module.
					state = persistence.GetBool(persistentVarName) ?? false;
				} else {
					// Otherwise it's a master module. But it still might have to follow the clicks on other copies of the same prop...
					if (!string.IsNullOrEmpty(perPodPersistenceName)) {
						state = persistence.GetBool(persistentVarName) ?? customGroupList[actionName];
					} else {
						state = customGroupList[actionName];
					}
				}
			} else {
				state = vessel.ActionGroups[groupList[actionName]];
			}

			// If needsElectricCharge is true and there is no charge, the state value is overridden to false and the click action is reexecuted.
			if (needsElectricChargeValue) {
				lightCheckCountdown--;
				if (lightCheckCountdown <= 0) {
					lightCheckCountdown = lightCheckRate;
					if (state && comp.ProcessVariable("ELECTRIC").MassageToDouble() < 0.01d) {
						Click();
						state = false;
					}
				}
			}

			if (state != oldState) {
				if (audioOutput != null && (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA ||
				    CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal)) {
					audioOutput.audio.Play();
				}
				if (anim != null) {
					if (state ^ reverse) {
						anim[animationName].normalizedTime = 0;
						anim[animationName].speed = 1f * customSpeed;
						anim.Play(animationName);
					} else {
						anim[animationName].normalizedTime = 1;
						anim[animationName].speed = -1f * customSpeed;
						anim.Play(animationName);
					}
				} else if (colorShiftRenderer != null) {
					colorShiftRenderer.material.SetColor(colorName, (state ^ reverse ? enabledColorValue : disabledColorValue));
				}
				oldState = state;
			}
		}

		public void LateUpdate()
		{
			if (JUtil.IsActiveVessel(vessel) && !startupComplete) {
				JUtil.AnnoyUser(this);
				// And disable ourselves.
				enabled = false;
			}
		}
	}
}

