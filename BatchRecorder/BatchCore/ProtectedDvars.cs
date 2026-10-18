using System;
using System.Collections.Generic;

namespace BatchRecorder
{
    /// <summary>
    /// The dvars Redacted refuses to "set" from the console (flag 0x80, cheat protection), read off the
    /// game's own table 2026-09-29 (docs\study\bo2-redacted-dvars-2026-09-29.tsv, 1195 of 2868). A CFG line
    /// for one of these is written into the dvar by RipperBridge; every other line goes in as a console
    /// command, so the game runs its own handling (the sun tweaks only take effect that way: SunFix, and
    /// measured again 2026-10-01 - written straight into memory, r_lightTweakSun* turned the picture black).
    /// Generated from the dump; regenerate if Redacted changes.
    /// </summary>
    public static class ProtectedDvars
    {
        static readonly HashSet<string> Names = new HashSet<string>((
            "ai_angularyawaccelrate ai_angularyawdecelfactor ai_angularyawenabled ai_debuganimdeltas ai_debugclaimednodes " +
            "ai_debugcoverentitynum ai_debugfindpath ai_debugfindpathdirect ai_debugfindpathlock ai_debugfindpathwidth " +
            "ai_debugmaymove ai_debugmeleeattackspots ai_debugthreatselection ai_disablespawn ai_eventdistbadplace " +
            "ai_eventdistbullet ai_eventdistbulletrunning ai_eventdistdeath ai_eventdistexplosion ai_eventdistfootstep " +
            "ai_eventdistfootsteplite ai_eventdistgrenadeping ai_eventdistgunshot ai_eventdistnewenemy ai_eventdistpain " +
            "ai_eventdistprojimpact ai_eventdistprojping ai_eventdistreact ai_eventdistsilencedshot " +
            "ai_foliageseethroughdist ai_friendlysuppression ai_friendlysuppressiondist ai_maxattackercount " +
            "ai_meleedamage ai_meleeheight ai_meleerange ai_meleewidth ai_moveorientmode ai_nododge ai_pathchokepointcost " +
            "ai_pathmomentum ai_pathnegotiationoverlapcost ai_pathrandompercent ai_showbadplaces ai_showcanshootchecks " +
            "ai_showdodge ai_showfailedpaths ai_shownearestnode ai_shownodedrawtype ai_shownodes ai_shownodesdist " +
            "ai_showpathfindnodes ai_showpaths ai_showpotentialthreatdir ai_showvisdata ai_showvisdatadist ai_showvolume " +
            "ai_slowdownmaxyawdiff ai_slowdownminrate ai_slowdownminyawdiff ai_slowdownrateblendfactor " +
            "ai_threatupdateinterval ai_trim_path_zigzags ai_turnrate ai_usebetterlookahead ai_usefacingtranslation " +
            "ai_useleanrunanimations airburstadjustdistance allemblemspurchased allemblemsunlocked allitemspurchased " +
            "allitemsunlocked bg_ads bg_aimspreadmovespeedthreshold bg_aqs bg_aqsstyle bg_blendtimeoverride bg_bobmax " +
            "bg_chargeshotautodischargedelay bg_chargeshotautofiredelay bg_chargeshotcenterspeedreductionperbullet " +
            "bg_chargeshotdamageincreaseperbullet bg_chargeshotdischargewhenqueuereachesmax " +
            "bg_chargeshotfirewhenqueuereachesmax bg_chargeshotmaxbulletqueueonemp bg_chargeshotmaxbulletsinqueue " +
            "bg_chargeshotmaxviewkick bg_chargeshotmincenterspeed bg_chargeshotpenetrationmultiplier " +
            "bg_chargeshotqueuetime bg_chargeshotuseoneammoformultiplebullets bg_chargeshotviewkickincreaseperbullet " +
            "bg_disableweaponplantinginwater bg_falldamagemaxheight bg_falldamageminheight bg_foliagesnd_fastinterval " +
            "bg_foliagesnd_maxspeed bg_foliagesnd_minspeed bg_foliagesnd_resetinterval bg_foliagesnd_slowinterval " +
            "bg_forceanimoverridetimercheck bg_forcedurationoverride bg_forceexplosivebullets bg_ladder_yawcap " +
            "bg_legyawcrouchtolerance bg_legyawpronetolerance bg_legyawtolerance bg_maxgrenadeindicatorspeed " +
            "bg_playeranimstanceallowmovementinterupttime bg_prone_yawcap bg_proneswingspeed bg_shieldhitencodeheightvm " +
            "bg_shieldhitencodeheightworld bg_shieldhitencodewidthvm bg_shieldhitencodewidthworld bg_shock_animation " +
            "bg_shock_lookcontrol bg_shock_lookcontrol_fadetime bg_shock_lookcontrol_maxpitchspeed " +
            "bg_shock_lookcontrol_maxyawspeed bg_shock_lookcontrol_mousesensitivityscale bg_shock_movement " +
            "bg_shock_screenblurblendfadetime bg_shock_screenblurblendtime bg_shock_screenflashshotfadetime " +
            "bg_shock_screenflashwhitefadetime bg_shock_screentype bg_shock_sound bg_shock_sounddrylevel " +
            "bg_shock_soundend bg_shock_soundendabort bg_shock_soundfadeintime bg_shock_soundfadeouttime " +
            "bg_shock_soundloop bg_shock_soundloopenddelay bg_shock_soundloopfadetime bg_shock_soundloopsilent " +
            "bg_shock_soundmodenddelay bg_shock_soundroomtype bg_shock_soundsnapshot bg_shock_soundwetlevel " +
            "bg_shock_viewkickfadetime bg_shock_viewkickperiod bg_shock_viewkickradius bg_shock_visionset_intime " +
            "bg_shock_visionset_name bg_shock_visionset_outtime bg_slopeframes bg_swingspeed bg_teleportaligntime " +
            "bg_vehicle_gravity bg_vehicle_ground_max_normal bg_vehicle_ground_min_normal bg_vehicle_max_body_pitch " +
            "bg_vehicle_max_body_roll bg_vehicle_max_pitch bg_vehicle_max_roll bg_vehicle_overclip bg_vehicle_pitch_track " +
            "bg_vehicle_roll_track bg_vehicle_slide_min_normal bg_vehicle_sphere_bounds_offset_z bg_vehicle_stepsize " +
            "bg_vehicle_tangential_clip_max_scale bg_vehicle_tangential_clip_vel_scale bg_vehicle_trace_offset " +
            "bg_viewbobamplitudedtp bg_viewbobamplitudeducked bg_viewbobamplitudeduckedads bg_viewbobamplitudeprone " +
            "bg_viewbobamplituderoll bg_viewbobamplitudesprinting bg_viewbobamplitudestanding " +
            "bg_viewbobamplitudestandingads bg_viewbobamplitudeswimming bg_viewkickmax bg_viewkickmin bg_viewkickrandom " +
            "bg_viewkickscale bg_vsmode_hud bg_weaponbobamplitudedtp bg_weaponbobamplitudeducked " +
            "bg_weaponbobamplitudeprone bg_weaponbobamplituderiding bg_weaponbobamplituderoll " +
            "bg_weaponbobamplituderolllaser bg_weaponbobamplitudesprinting bg_weaponbobamplitudestanding " +
            "bg_weaponbobamplitudeswimming bg_weaponbobfrequencyswimming bg_weaponbobheavyweaponscalar bg_weaponboblag " +
            "bg_weaponbobmax bg_weaponoffscreenreloadscale bot_closedistance bot_crouchdistance bot_fov bot_goalradius " +
            "bot_maxadstime bot_maxcrouchtime bot_maxdeathtime bot_maxfiretime bot_maxgrenadetime bot_maxpitchtime " +
            "bot_maxreactiontime bot_maxstrafetime bot_minadstime bot_mincrouchtime bot_mindeathtime bot_minfiretime " +
            "bot_mingrenadetime bot_minpitchtime bot_minreactiontime bot_minstrafetime bot_pitchdown bot_pitchspeed " +
            "bot_pitchspeedads bot_pitchup bot_sprintdistance bot_strafechance bot_targetleadbias bot_yawspeed " +
            "bot_yawspeedads bullet_penetrationenabled bullet_penetrationminfxdist bullet_ricochetbasechance " +
            "camerashakeremotehelo_angles camerashakeremotehelo_freqs camerashakeremotehelo_speedrange " +
            "camerashakeremotemissile_angles camerashakeremotemissile_freqs camerashakeremotemissile_speedrange " +
            "cg_adszscalemax cg_airburstcamerarestriction cg_airburstlaseflashtime cg_airburstlasetime " +
            "cg_airburststraferestriction cg_allplayernamesvisible cg_artillerykillcambackdist cg_artillerykillcamfov " +
            "cg_artillerykillcamgroundbackdist cg_artillerykillcamgroundupdist cg_artillerykillcamupdist " +
            "cg_artillerykillcamwalloutdist cg_artillerykillcamwallsidedist cg_ballisticarc_beginwidth " +
            "cg_ballisticarc_drawdelay cg_ballisticarc_endwidth cg_ballisticarc_maxbounces cg_ballisticarc_minbounces " +
            "cg_ballisticarc_offset cg_cameraspikeenemycolor cg_cameraspikehighlightbrightness " +
            "cg_cameravehicleexittweentime cg_camerawaterclip cg_canseefriendlyfrustumexpand " +
            "cg_canseefriendlyfrustummindistance cg_canseefriendlyfrustumupdateinterval cg_centertime cg_colorhue " +
            "cg_colorsaturation cg_colorscale cg_colortemp cg_constantsizeheadicons cg_corpsehighlightfadetime " +
            "cg_crosshairalpha cg_crosshairalphamin cg_crosshairdynamic cg_crosshairenemycolor cg_cullbulletangle " +
            "cg_cullbullets cg_debug_overlay_viewport cg_debugevents cg_debugposition cg_destructiblekillcamclosexydist " +
            "cg_destructiblekillcamclosezdist cg_destructiblekillcamfarblur cg_destructiblekillcamfarblurdist " +
            "cg_destructiblekillcamfarblurstart cg_destructiblekillcamfov cg_destructiblekillcamnearblur " +
            "cg_destructiblekillcamnearblurend cg_destructiblekillcamnearblurstart cg_destructiblekillcamregularheight " +
            "cg_destructiblekillcamzincrease cg_disableplayernames cg_dogkillcamdistfromeyes cg_dogkillcamforwarddist " +
            "cg_dogkillcamfov cg_dogkillcamsidedist cg_dogkillcamupdist cg_dogkillmindistfromtarget cg_draw2d " +
            "cg_drawcrosshair cg_drawcrosshairnames cg_drawcrosshairnamesposx cg_drawcrosshairnamesposy " +
            "cg_drawfriendlynames cg_drawgun cg_drawhealth cg_drawmaterial cg_drawmaterialimagename " +
            "cg_drawmaterialimagenum cg_drawmodelaxis cg_drawshellshock cg_drawthroughwalls cg_dronekillcambackdist " +
            "cg_dronekillcamupdist cg_dumpanims cg_enablehelicopternoculllodout cg_enemynamefadein cg_enemynamefadeout " +
            "cg_explosivekillcambackdist cg_explosivekillcamgroundbackdist cg_explosivekillcamgroundupdist " +
            "cg_explosivekillcamstopdeceldist cg_explosivekillcamstopdist cg_explosivekillcamupdist " +
            "cg_explosivekillcamwalloutdist cg_explosivekillcamwallsidedist cg_fakefirewizbychance " +
            "cg_firstpersontracerchance cg_flarevisionsetfadeduration cg_footprints cg_footprintsdebug " +
            "cg_footprintsdistortwater cg_footsteps cg_forceinfrared cg_fov cg_fovextracam cg_fovmin cg_fovscale " +
            "cg_friendlynamefadein cg_friendlynamefadeout cg_fuelhudversion cg_gun_move_f cg_gun_move_minspeed " +
            "cg_gun_move_r cg_gun_move_rate cg_gun_move_u cg_gun_ofs_f cg_gun_ofs_r cg_gun_ofs_u cg_gun_rot_minspeed " +
            "cg_gun_rot_p cg_gun_rot_r cg_gun_rot_rate cg_gun_rot_y cg_gun_x cg_gun_y cg_gun_z cg_helikillcamfarblur " +
            "cg_helikillcamfarblurdist cg_helikillcamfarblurstart cg_helikillcamfov cg_helikillcamnearblur " +
            "cg_helikillcamnearblurend cg_helikillcamnearblurstart cg_hudgrenadeiconmaxrangeflash " +
            "cg_hudgrenadeiconmaxrangefrag cg_hudmapradarlinethickness cg_infraredblur cg_infraredblurtime " +
            "cg_infraredfadeouttime cg_infraredusedefaultmaterial cg_killcamdebug cg_lasersightmaxdistance " +
            "cg_maxplayerhighlighttargetsize cg_microwaveturretkillcambackoffset cg_microwaveturretkillcamfov " +
            "cg_microwaveturretkillcamheightincrease cg_mincullbulletdist cg_missile_fov " +
            "cg_opticattachmentviewmodelswitch cg_overheadnamesfardist cg_overheadnamesfarscale cg_overheadnamesglow " +
            "cg_overheadnamesmaxdist cg_overheadnamesneardist cg_overheadnamestagupdateinterval " +
            "cg_playerfrustumhalfheight cg_playerhighlightblinktime cg_playerhighlightbrightness " +
            "cg_playerhighlightenemycolor cg_playerhighlightminfade cg_playerhighlighttargetsize " +
            "cg_pronefeetcollisionhull cg_rangefinderactiveheight cg_rangefinderactivewidth " +
            "cg_rangefinderdiamondindicator cg_rangefinderdiamondsize cg_rangefinderminenemyspottingdist " +
            "cg_rangefinderplayertargetsize cg_retrievehinttime cg_retrievehinttimestuck cg_rocketkillcambackdist " +
            "cg_rocketkillcamupdist cg_scriptedkillcamclosexydist cg_scriptedkillcamclosezdist cg_scriptedkillcamfarblur " +
            "cg_scriptedkillcamfarblurdist cg_scriptedkillcamfarblurstart cg_scriptedkillcamfov " +
            "cg_scriptedkillcamnearblur cg_scriptedkillcamnearblurend cg_scriptedkillcamnearblurstart " +
            "cg_sonarattachmentblur cg_sonarattachmentcountercolormultiplier cg_sonarattachmentcountermaxspeed " +
            "cg_sonarattachmentdistancefactor cg_sonarattachmentempfade cg_sonarattachmentfadefriendlies " +
            "cg_sonarattachmenthidefriendlies cg_sonarattachmentmaxdist cg_sonarattachmentmaxspeed " +
            "cg_sonarattachmentpulseinterval cg_sonarattachmentpulsetime cg_sonarattachmentspeeddelay " +
            "cg_sonarattachmentspeedtimeratio cg_sonarattachmentzmturnedfadetime cg_sonarattachmentzmturnedgracetime " +
            "cg_sonarattachmentzmturnedminfade cg_thirdperson cg_thirdpersonangle cg_thirdpersonfocusdist " +
            "cg_thirdpersonmode cg_thirdpersonrange cg_timeddamageduration cg_treadmarks cg_turretbipodoffset " +
            "cg_turretkillcambackoffset cg_turretkillcamclosexydist cg_turretkillcamclosezdist cg_turretkillcamfarblur " +
            "cg_turretkillcamfarblurdist cg_turretkillcamfarblurstart cg_turretkillcamfov cg_turretkillcamheightincrease " +
            "cg_turretkillcamnearblur cg_turretkillcamnearblurend cg_turretkillcamnearblurstart " +
            "cg_turretkillcamsideoffset cg_ufo_scaler cg_use_colored_smoke cg_usecolorcontrol " +
            "cg_useweaponbasedvariablezoom cg_useweaponswitchreloadcancel cg_usingclientscripts " +
            "cg_velocityarrow_maxsegmentlength cg_velocityarrow_minsegmentlength cg_velocityarrow_rateofchange " +
            "cg_viewvehicleinfluencegunner cg_viewvehicleinfluencegunnerfiring cg_viewzsmoothingmax cg_viewzsmoothingmin " +
            "cg_viewzsmoothingtime cg_visionsetlerpmaxdecreaseperframe cg_visionsetlerpmaxincreaseperframe " +
            "cg_watersheeting cg_weaponheat cl_nodelta com_freemovescale com_timescale compassclampicons " +
            "compassenemyfootstepenabled compassenemyfootstepmaxrange compassenemyfootstepmaxz " +
            "compassenemyfootstepminspeed compassforceplayericon compassgridalign compassgridcols compassgridenabled " +
            "compassgridrows compasslocalradarradius compasslocalradarupdatetime compassminradius compassminrange " +
            "compassradarlinethickness compassradarpingfadetime compassradarupdatefasttime compassradarupdatetime " +
            "compasssatellitepingfadetime compasssatellitescantime compasssatellitestaticimagefadetime compassshowenemies " +
            "compasssoundpingfadetime compassstaticimageupdatetime debugrendercollisiondistance debugrendermask " +
            "defaultdamageduration defaultdamageinterval defaulthitdamage disable_rope disablelookatentitylogic " +
            "dog_turn180_angle dog_turn90_angle dog_turn_min_goal_dist door_breach_weapondrop doublesided_raycasts dtp " +
            "dtp_exhaustion_window dtp_fall_damage_max_height dtp_fall_damage_min_height dtp_max_apex_duration " +
            "dtp_max_slide_addition dtp_max_slide_duration dtp_min_speed dtp_new_trajectory dtp_new_trajectory_multiplier " +
            "dtp_post_move_pause dtp_startup_delay dynent_bulletforce dynent_damageradiusscale dynent_explodeforce " +
            "dynent_explodemaxents dynent_explodeminforce dynent_explodespinscale dynent_explodeupbias " +
            "dynentpieces_angularvelocity dynentpieces_impactforce dynentpieces_velocity enable_new_prone_check fixedtime " +
            "fixedtime_float flame_config_valid flame_debug_render flame_kick_offset flame_kick_recover_speed " +
            "flame_kick_speed flame_render flame_team_damage flame_test flame_use_dvars flaredisableeffects friction " +
            "fs_ignorelocalized g_actorandvehicleantilag g_actorandvehicleantilagdebug g_ai g_aieventdump g_avoidenabled " +
            "g_compassshowenemies g_customteamname_three g_debugbullets g_debugdamage g_debuglocdamage " +
            "g_debuglocdamageentity g_debuglochit g_debuglochittime g_disableeliteemblemuploads " +
            "g_disableeliteregistrationpopup g_dumpanims g_dumpanimscommands g_entinfo g_entinfo_aitext g_entinfo_maxdist " +
            "g_entinfo_scale g_entinfo_type g_erroronpathsnotconnected g_fogcolorreadonly g_foghalfdistreadonly " +
            "g_fogstartdistreadonly g_friendlyfiredist g_friendlynamedist g_mantleblocktimebuffer g_mingrenadedamagespeed " +
            "g_scorescolor_allies g_scorescolor_axis g_scorescolor_enemyteam g_scorescolor_free g_scorescolor_myteam " +
            "g_scorescolor_spectator g_spawnai g_teamcolor_allies g_teamcolor_axis g_teamcolor_enemyteam " +
            "g_teamcolor_enemyteamalt g_teamcolor_free g_teamcolor_myteam g_teamcolor_myteamalt " +
            "g_teamcolor_myteamalt_party g_teamcolor_party g_teamcolor_spectator g_teamname_allies g_teamname_axis " +
            "g_teamname_three g_turretbipodoffset g_turretserverpitchmax g_turretserverpitchmin g_useholdspawndelay " +
            "g_vehicledebug g_vehicledrawpath g_vehicledrawsplines g_vehicleplanecurvetime gpad_button_deadzone " +
            "gpad_debug gpad_stick_deadzone_max gpad_stick_deadzone_min gpad_stick_pressed gpad_stick_pressed_hysteresis " +
            "grenadebouncerestitutionmax grenadebumpfreq grenadebumpmag grenadebumpmax grenadecurvemax " +
            "grenadefrictionhigh grenadefrictionlow grenadefrictionmaxthresh grenaderestthreshold grenaderollingenabled " +
            "grenadewobblefreq grenadewobblefwdmag grenadewobblesidedamp grenadewobblesidemag hatch_mantle_fwd " +
            "hatch_mantle_up hudelempausedbrightness ik_ai_hand_get_data ik_ai_hand_offset_vec ik_ai_hand_rotation_vec " +
            "ik_ai_hand_tuning ik_ai_pool_size ik_ai_range_max ik_debug ik_enable ik_enable_ai_hand ik_enable_ai_terrain " +
            "ik_enable_player_hand ik_enable_player_terrain ik_foot_test ik_hand_test ik_left_hand_lerp_test " +
            "ik_paranoid_matrix_checks ik_pelvis_test ik_right_hand_lerp_test jump_height jump_ladderpushvel " +
            "jump_slowdownenable jump_spreadadd jump_stepsize laserdebug laserendoffset laserflarepct laserflarepct_alt " +
            "laserlightbeginoffset laserlightbodytweak laserlightendoffset laserlightradius laserlightradius_alt " +
            "laserlightrangeplayer laserradius laserradius_alt laserrange laserrange_alt laserrangeplayer lui_timescale " +
            "mantle_adjustment_tu mantle_check_angle mantle_check_glass_extra_range mantle_check_radius " +
            "mantle_check_range mantle_check_range_mp mantle_debug mantle_enable mantle_view_yawcap " +
            "mantle_weapon_anim_height mantle_weapon_height melee_debug missiledroneaccelclimb missiledroneacceldescend " +
            "missiledroneclimbangledirect missiledroneclimbangletop missiledroneclimbceilingdirect " +
            "missiledroneclimbceilingtop missiledroneclimbheightdirect missiledroneclimbheighttop " +
            "missiledroneclimbmaxdist missiledroneclimbtoowner missiledronedistanceavoidance " +
            "missiledronedistanceavoidancesec missiledronedistsqtargetavoidancedisabled missiledroneflattenrollratio " +
            "missiledronespeedlimitclimb missiledronespeedlimitdescend missiledronespeedmin missiledroneturndecel " +
            "missiledroneturnmaxrate missiledroneturnmaxroll missiledroneturnmaxrollchange missiledroneturnrateascent " +
            "missiledroneturnrateavoidance missiledroneturnratedecent missiledroneturnratesmalladjustments " +
            "missilehellfiremaxslope missilehellfireupaccel missilejavaccelclimb missilejavacceldescend " +
            "missilejavclimbangledirect missilejavclimbangletop missilejavclimbceilingdirect missilejavclimbceilingtop " +
            "missilejavclimbheightdirect missilejavclimbheighttop missilejavclimbtoowner missilejavspeedlimitclimb " +
            "missilejavspeedlimitdescend missilejavturndecel missilejavturnratedirect missilejavturnratetop " +
            "missilemolotovblobnum missilemolotovblobtime missileplantablesize missiletvguidedboost " +
            "missiletvguidedboostspeeddown missiletvguidedboostspeedup missiletvguidedflatteningrollaccel " +
            "missiletvguidedmaxrollangle missiletvguidedmpspecific missiletvguidedstickdeadzone " +
            "missiletvguidedturningrollaccel missiletvguidedturnrate missilewatermaxdepth perk_armorpiercing " +
            "perk_blackbirdshowsgpsjammer perk_bulletpenetrationmultiplier perk_damagekickreduction " +
            "perk_deathstreakcountrequired perk_delayexplosivetime perk_disarmexplosivetime perk_dogsattackghost " +
            "perk_extrabreath perk_fastladderclimbmultiplier perk_fireproof perk_flakjacket perk_flakjacket_hardcore " +
            "perk_gpsjammer_alpha perk_gpsjammer_fade_time perk_gpsjammer_graceperiods perk_gpsjammer_min_distance " +
            "perk_gpsjammer_min_speed perk_gpsjammer_time_period perk_grenadedeath perk_grenadetossbacktimer " +
            "perk_healthregenmultiplier perk_interactspeedmultiplier perk_killstreakdeathpenaltymultiplier " +
            "perk_killstreakmomentummultiplier perk_killstreakreduction perk_mantlereduction perk_marksmanenemynamefadein " +
            "perk_marksmanenemynamefadeout perk_nottargetedbyai_graceperiod perk_nottargetedbyai_min_speed " +
            "perk_scavenger_clip_multiplier perk_scavenger_lethal_proc perk_scavenger_tactical_proc " +
            "perk_shellshockreduction perk_speedmultiplier perk_sprintmultiplier perk_sprintrecoverymultiplier " +
            "perk_throwbackinnerradius perk_weapadsmultiplier perk_weapequipmentusemultiplier perk_weapmeleemultiplier " +
            "perk_weapratemultiplier perk_weapreloadmultiplier perk_weapspreadmultiplier perk_weapswitchmultiplier " +
            "perk_weaptossmultiplier pickupprints player_adsexitdelay player_animrunthreshhold player_animwalkthreshhold " +
            "player_backspeedscale player_bayonetlaunchdebugging player_bayonetlaunchproof player_bayonetlaunchzcap " +
            "player_bayonetrange player_bayonettargetdist player_breath_fire_delay player_breath_gasp_lerp " +
            "player_breath_gasp_scale player_breath_gasp_time player_breath_hold_lerp player_breath_hold_time " +
            "player_breath_snd_delay player_breath_snd_lerp player_clipsizemultiplier player_debughealth " +
            "player_dmgtimer_flinchtime player_dmgtimer_maxtime player_dmgtimer_minscale player_dmgtimer_stumbletime " +
            "player_dmgtimer_timeperpoint player_enableshuffleanims player_endurancespeedscale player_footstepsthreshhold " +
            "player_knockbackmovethreshhold player_laststandbleedouttime player_laststandbleedouttimenorevive " +
            "player_laststandhealthoverlaytime player_lean_rotate player_lean_rotate_crouch player_lean_shift " +
            "player_lean_shift_crouch player_meleecalcspeedfromtarget player_meleechargefriction " +
            "player_meleechargemaxspeed player_meleeheight player_meleewidth player_mguseradius " +
            "player_move_factor_on_torso player_movethreshhold player_reviveplayerlistcycletime " +
            "player_revivetriggerradius player_runbkthreshhold player_runthreshhold player_scopeexitondamage " +
            "player_slick_accel player_slick_invspeed player_slick_wishspeed player_sliding_friction " +
            "player_sliding_velocity_cap player_sliding_wishspeed player_slopeanimangle player_spectatespeedscale " +
            "player_sprintcamerabob player_sprintmintime player_sprintrechargepause player_sprintspeedscale " +
            "player_sprintstrafespeedscale player_sprintthreshhold player_standingviewheight player_strafeanimcosangle " +
            "player_strafespeedscale player_sustainammo player_swimdamage player_swimdamagerinterval player_swimtime " +
            "player_throwbackinnerradius player_throwbackouterradius player_throwbackouterradius_zm player_turnanims " +
            "player_useradius player_useradius_zm player_view_pitch_down player_view_pitch_up player_waterspeedscale " +
            "player_zombiemeleeheight player_zombiemeleewidth player_zombiespeedscale player_zombiesprintspeedscale " +
            "popinletterfxtime prone_bipod_enable r_alphamap r_autolodscale r_blinkcullnone r_blinklayers " +
            "r_blinklitquasiopaque r_blinktrans r_blur r_brightness r_brushlimit r_colormap r_contrast r_debugmetalstorm " +
            "r_debugperformance r_debugshader r_debugshowcoronas r_debugshowdynamiclights r_debugshowprimarylights " +
            "r_desaturation r_disablegenericfilter r_dobjlimit r_dof_bias r_dof_farblur r_dof_farend r_dof_farstart " +
            "r_dof_nearblur r_dof_nearend r_dof_nearstart r_dof_showdebug r_dof_tweak r_dof_viewmodelend " +
            "r_dof_viewmodelstart r_dpvs_usecellforceinvisiblebits r_drawdebugfogparams r_drawlightmapdrawlists " +
            "r_drawlitdrawlists r_drawwater r_enableoccluders r_enableplayershadow r_filmusetweaks " +
            "r_flamefx_distortionscalefactor r_flamefx_fps r_flamefx_magnitude r_fog r_fog_disable r_fogbasedist " +
            "r_fogbaseheight r_fogcolor r_foghalfdist r_foghalfheight r_fogopacity r_fogsuncolor r_fogsuninner " +
            "r_fogsunopacity r_fogsunouter r_fogsunpitch r_fogsunyaw r_fogtweak r_forcecamerahdg r_forcecameranode " +
            "r_forcecamerarecord r_forcelod r_fullbright r_fullprepass r_glossmap r_grasswindforceenable r_herolightscale " +
            "r_highloddist r_lightconflicts r_lightmapfilterdisable r_lightmaponlybspsurfs r_lightmaponlyentsurfs " +
            "r_lightmaponlysmodelsurfs r_lightmapsecondary r_lockpvs r_lockpvsincode r_lodbiasrigid r_lodbiasskinned " +
            "r_lodscalerigid r_lodscaleskinned r_lowestloddist r_lowloddist r_mediumloddist r_missile_cam_debug_display " +
            "r_modellimit r_modelvertcolor r_multi_extracam_debug r_norefresh r_normalmap r_occlusionmap " +
            "r_poisonfx_blurmax r_portalminrecursedepth r_portalwalklimit r_primarylighttweakdiffusestrength " +
            "r_primarylighttweakspecularstrength r_primarylightusetweaks r_qrcode_debug_display " +
            "r_qrcode_debug_display_size r_remotescreen_renderlastframe r_resamplescene r_revivefx_debug r_scaleviewport " +
            "r_seethru_decal_enable r_shader_constant_set_debug_range r_shader_constant_set_enable " +
            "r_showforcedinvisiblecells r_showlightgrid r_showlightingorigins r_showmissinglightgrid " +
            "r_showoutdoorvolumedebug r_showpenetration r_showreflectionprobeselection r_showsundirectiondebug " +
            "r_singlecell r_skippvs r_sky_intensity_showdebugdisplay r_sortbacktofront r_sortdrawsurfsbsp " +
            "r_sortdrawsurfsstaticmodel r_specularmap r_spotlightentityshadows r_spotlightshadows " +
            "r_spotlightsmodelshadows r_sun_from_dvars r_swrk_override_charactercharredamount " +
            "r_swrk_override_characterdissolvecolor r_swrk_override_wetness r_tension_enable r_texfilterdisable " +
            "r_texfiltermipbias r_ui3d_debug_display r_ui3d_h r_ui3d_use_debug_values r_ui3d_w r_ui3d_x r_ui3d_y " +
            "r_waterfogtest r_zbufferdebug r_zbufferdebugrange r_zebrastripe r_zfar r_znear radius_damage_debug " +
            "ragdoll_baselerp_time ragdoll_bullet_force ragdoll_bullet_upbias ragdoll_debug ragdoll_explode_force " +
            "ragdoll_explode_upbias ragdoll_fps ragdoll_jitter_scale ragdoll_jointlerp_time ragdoll_rotvel_scale " +
            "ragdoll_self_collision_scale redactiondisplaytime redactionfadeduration redactionstartstroketime " +
            "redactionstroketime revive_time_taken riotshield_assist_time riotshield_bullet_damage_scale " +
            "riotshield_damage_score_max riotshield_damage_score_threshold riotshield_deploy_limit_radius " +
            "riotshield_deploy_pitch_max riotshield_deploy_roll_max riotshield_deploy_zdiff_max " +
            "riotshield_deployed_health riotshield_destroyed_cleanup_time riotshield_explosive_damage_scale " +
            "riotshield_melee_damage_scale riotshield_projectile_damage_scale shieldblastdamageprotection_120 " +
            "shieldblastdamageprotection_180 shieldblastdamageprotection_30 shieldblastdamageprotection_60 " +
            "shielddeployshakeduration shielddeployshakescale shieldimpactbulletshakeduration " +
            "shieldimpactbulletshakescale shieldimpactexplosionhighshakeduration shieldimpactexplosionhighshakescale " +
            "shieldimpactexplosionlowshakeduration shieldimpactexplosionlowshakescale shieldimpactexplosionthreshold " +
            "shieldimpactmissileshakeduration shieldimpactmissileshakescale shieldplayerbulletprotectiondegrees " +
            "showvisionsetdebuginfo sm_fastsunshadow sm_polygonoffsetbias sm_spotshadowlargeradiusscale sm_strictcull " +
            "sm_sunalwayscastsshadow sm_sunshadowcenter sm_sunshadowscale snd_autosim snd_autosim_window " +
            "snd_boat_current_rpm snd_boat_engine_off snd_boat_lerp_rpm snd_boat_pitch_high_max snd_boat_pitch_high_min " +
            "snd_boat_pitch_idle_max snd_boat_pitch_idle_min snd_boat_pitch_low_max snd_boat_pitch_low_min " +
            "snd_boat_pitch_med_max snd_boat_pitch_med_min snd_boat_rpm_high_fin_end snd_boat_rpm_high_start " +
            "snd_boat_rpm_idle_end snd_boat_rpm_idle_fout_start snd_boat_rpm_low_end snd_boat_rpm_low_fin_end " +
            "snd_boat_rpm_low_fout_start snd_boat_rpm_low_start snd_boat_rpm_med_end snd_boat_rpm_med_fin_end " +
            "snd_boat_rpm_med_fout_start snd_boat_rpm_med_start snd_boat_rpm_scalar snd_boat_using_lerp_rpm " +
            "snd_boat_water_fast_min snd_boat_water_idle_max snd_boat_water_pitch_max snd_boat_water_slow_max " +
            "snd_boat_water_slow_min snd_draw3d snd_futz snd_jetgun_loop_start snd_jetgun_pitch_end " +
            "snd_jetgun_pitch_start snd_max_ram_voice snd_max_stream_voice snd_minigun_loop_start snd_minigun_pitch_end " +
            "snd_minigun_pitch_start snd_ps3_vol_occlusion_attenuation_dry snd_ps3_vol_occlusion_attenuation_wet " +
            "snd_throttle_reduce_vol snd_throttle_time_held_down snd_trace_master snd_trace_reverb snd_trace_voice " +
            "spawnsystem_badspawn_force_record spawnsystem_debug spawnsystem_debug_archive spawnsystem_debug_best_points " +
            "spawnsystem_debug_influencer_pulse spawnsystem_debug_influencer_types spawnsystem_debug_influencers " +
            "spawnsystem_debug_liveedit spawnsystem_debug_player spawnsystem_debug_point_weights spawnsystem_debug_points " +
            "spawnsystem_debug_showclients spawnsystem_debug_sideswitched spawnsystem_debug_team " +
            "spawnsystem_debug_visibility spawnsystem_debug_visibility_time spawnsystem_demo_enable_parsing stopspeed " +
            "timescale tu13_bg_enablebulletweaponbounce tu6_dynent_disable_rb_collision " +
            "tu6_dynent_small_cylinder_dimension tu6_dynent_small_cylinder_max_avel tu7_3rdpersonspecatefix " +
            "turret_killstreaktargettime turret_sentrytargettime turret_targetleadbias turretplayeravoidscale " +
            "typewritercod7letterfxtime ui_allow_classchange ui_animspeedscale ui_hud_hardcore ui_hud_visible " +
            "ui_menulvlnotify vehanim_debug vehanim_enable vehgunnersplashdamage vehhelicopterboundmapheight " +
            "vehhelicopterboundmaplowerrightx vehhelicopterboundmaplowerrighty vehhelicopterboundmapupperleftx " +
            "vehhelicopterboundmapupperlefty vehhelicopterboundson vehhelicopterfreelook vehhelicopterhoverspeedthreshold " +
            "vehhelicopterinvertupdown vehhelicopterlookaheadtime vehhelicopterpathtransitiontime " +
            "vehhelicopterrightstickdeadzone vehhelicopterscalemovement vehhelicoptersoftcollisions " +
            "vehhelicopterstrafedeadzone vehhelicopteryawonleftstick vehicle_collision_prediction_time " +
            "vehicle_damage_bouncing_betty vehicle_damage_bullet vehicle_damage_grenade vehicle_damage_max_shielding " +
            "vehicle_damage_projectile vehicle_damage_satchel_charge vehicle_damage_sticky_grenade " +
            "vehicle_damage_zone_front vehicle_damage_zone_rear vehicle_damage_zone_side vehicle_damage_zone_under " +
            "vehicle_destructible_damage_bouncing_betty vehicle_destructible_damage_bouncing_betty_radius " +
            "vehicle_destructible_damage_grenade vehicle_destructible_damage_grenade_radius " +
            "vehicle_destructible_damage_projectile_radius vehicle_destructible_damage_satchel_charge " +
            "vehicle_destructible_damage_satchel_charge_radius vehicle_destructible_damage_sticky_grenade " +
            "vehicle_destructible_damage_sticky_grenade_radius vehicle_piece_damagesfx_threshold " +
            "vehicle_push_during_mantle vehicle_switch_seat_delay vehicle_useradius vehlocationalvehicleseatentry " +
            "vehlockturrettoplayerview vehplanefakeliftforce vehplanegravityforce vehplaneliftforce vehplanelowspeed " +
            "vehplanepitchaccel vehplanerollaccel vehplanerolldeadzone vehplaneyawfromrollscale vehplaneyawspeed " +
            "waterbrush_entity waypointdistfade waypointplayeroffsetcrouch waypointplayeroffsetprone " +
            "waypointplayeroffsetrevive waypointplayeroffsetstand waypointtimefade wind_debug_display " +
            "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        public static bool Contains(string name) { return Names.Contains(name); }
    }
}
