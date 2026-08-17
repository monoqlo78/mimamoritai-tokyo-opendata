import { entity, role, text, date, uuid } from '@microsoft/rayfin-core';

/**
 * Hourly rollup of the outdoor observation, mirrored from mimamori.HeatReadings.
 *
 * The console already shows how much electricity a home used, but watt-hours on
 * their own say nothing about whether a household is at risk: an air conditioner
 * that is off is only alarming when it is hot outside. This table is the other
 * half of that sentence.
 *
 * The grain is (observation point, UTC hour) -- deliberately not per household.
 * The source is 環境省 WBGT and 気象庁 AMeDAS, which are public observations for a
 * point, so attaching them to a household would imply a per-home measurement we
 * do not have. Households reference the point through their AMeDAS station.
 *
 * Nothing here is personal: it is public open data, and no household id, resident
 * or device appears on this table at all.
 *
 * Empty strings mean "not observed", which is never the same as a measured zero.
 * WBGT in particular is only published from late April to late October, so the
 * winter rows legitimately carry a temperature and no 暑さ指数.
 */
@entity()
@role('authenticated', 'read')
export class OutdoorReading {
  @uuid() id!: string;

  /** 観測地点コード, e.g. "44132" (東京). */
  @text({ max: 20 }) pointCode!: string;

  /** 地点名, e.g. "東京". Empty when the source did not name the point. */
  @text({ max: 100 }) areaName!: string;

  /** Start of the UTC hour this bucket covers. */
  @date() bucketStart!: Date;

  /** Mean air temperature in °C over the hour. Empty when never observed. */
  @text({ max: 20 }) temperatureC!: string;

  /** Lowest / highest observation in the hour, so the console can draw a band. */
  @text({ max: 20 }) minTemperatureC!: string;
  @text({ max: 20 }) maxTemperatureC!: string;

  /** Mean relative humidity in %. Empty when never observed. */
  @text({ max: 20 }) humidityPercent!: string;

  /**
   * Highest 暑さ指数 (WBGT) in the hour, in °C. The maximum rather than the mean
   * because the risk a family is warned about is the worst moment, not the average.
   * Empty out of season.
   */
  @text({ max: 20 }) maxWbgt!: string;

  /** Highest 環境省 heat band seen in the hour (0 = unknown/none). */
  @text({ max: 10 }) heatLevel!: string;

  /** Highest cold band seen in the hour (0 = unknown/none). */
  @text({ max: 10 }) coldLevel!: string;

  /** Observations behind this bucket, so a single stray sample is visible as such. */
  @text({ max: 10 }) sampleCount!: string;
}
