import { ActivityBucket } from './ActivityBucket.js';
import { AiRouterCall } from './AiRouterCall.js';
import { AlertRecord } from './AlertRecord.js';
import { HouseholdSnapshot } from './HouseholdSnapshot.js';
import { OutdoorReading } from './OutdoorReading.js';

/**
 * Every entity must be listed here: Rayfin derives the SQL database schema and
 * the generated GraphQL API from this map, so an entity file that is not
 * registered simply does not exist at runtime.
 */
export type MimamoriAdminSchema = {
  HouseholdSnapshot: HouseholdSnapshot;
  AlertRecord: AlertRecord;
  ActivityBucket: ActivityBucket;
  AiRouterCall: AiRouterCall;
  OutdoorReading: OutdoorReading;
};

export const schema = [HouseholdSnapshot, AlertRecord, ActivityBucket, AiRouterCall, OutdoorReading];
